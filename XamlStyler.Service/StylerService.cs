﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using XamlStyler.Core.Helpers;
using XamlStyler.Core.Model;
using XamlStyler.Core.Options;
using XamlStyler.Core.Parser;
using XamlStyler.Core.Reorder;

namespace XamlStyler.Core
{
    public class StylerService
    {
        private readonly Regex _htmlReservedCharRegex = new Regex(@"&([\d\D][^;]{3,7});");
        private readonly Regex _htmlReservedCharRestoreRegex = new Regex(@"__amp__([^;]{2,7})__scln__");
        private readonly Stack<ElementProcessStatus> _elementProcessStatusStack;

        private IStylerOptions Options { get; set; }
        private IList<string> NoNewLineElementsList { get; set; }
        private AttributeOrderRules OrderRules { get; set; }
        private List<NodeReorderService> ReorderServices { get; set; }

        private StylerService()
        {
            _elementProcessStatusStack = new Stack<ElementProcessStatus>();
        }

        private void Initialize()
        {
            ReorderServices = new List<NodeReorderService>
            {
                GetReorderGridChildrenService(),
                GetReorderCanvasChildrenService(),
                GetReorderSettersService()
            };
        }

        private NodeReorderService GetReorderGridChildrenService()
        {
            var reorderService = new NodeReorderService { IsEnabled = Options.ReorderGridChildren };
            reorderService.ParentNodeNames.Add(new NameMatch("Grid", null));
            reorderService.ChildNodeNames.Add(new NameMatch(null, null));
            reorderService.SortByAttributes.Add(new SortAttribute("Grid.Row", null, true, x => x.Name.LocalName.Contains(".") ? "-2" : "-1"));
            reorderService.SortByAttributes.Add(new SortAttribute("Grid.Column", null, true, x => "-1"));
            return reorderService;
        }

        private NodeReorderService GetReorderCanvasChildrenService()
        {
            var reorderService = new NodeReorderService { IsEnabled = Options.ReorderCanvasChildren };
            reorderService.ParentNodeNames.Add(new NameMatch("Canvas", null));
            reorderService.ChildNodeNames.Add(new NameMatch(null, null));
            reorderService.SortByAttributes.Add(new SortAttribute("Canvas.Left", null, true, x => "-1"));
            reorderService.SortByAttributes.Add(new SortAttribute("Canvas.Top", null, true, x => "-1"));
            reorderService.SortByAttributes.Add(new SortAttribute("Canvas.Right", null, true, x => "-1"));
            reorderService.SortByAttributes.Add(new SortAttribute("Canvas.Bottom", null, true, x => "-1"));
            return reorderService;
        }

        private NodeReorderService GetReorderSettersService()
        {
            var reorderService = new NodeReorderService();
            reorderService.ParentNodeNames.Add(new NameMatch("DataTrigger", null));
            reorderService.ParentNodeNames.Add(new NameMatch("MultiDataTrigger", null));
            reorderService.ParentNodeNames.Add(new NameMatch("MultiTrigger", null));
            reorderService.ParentNodeNames.Add(new NameMatch("Style", null));
            reorderService.ParentNodeNames.Add(new NameMatch("Trigger", null));
            reorderService.ChildNodeNames.Add(new NameMatch("Setter", "http://schemas.microsoft.com/winfx/2006/xaml/presentation"));

            switch (Options.ReorderSetters)
            {
                case ReorderSettersBy.None:
                    reorderService.IsEnabled = false;
                    break;
                case ReorderSettersBy.Property:
                    reorderService.SortByAttributes.Add(new SortAttribute("Property", null, false));
                    break;
                case ReorderSettersBy.TargetName:
                    reorderService.SortByAttributes.Add(new SortAttribute("TargetName", null, false));
                    break;
                case ReorderSettersBy.TargetNameThenProperty:
                    reorderService.SortByAttributes.Add(new SortAttribute("TargetName", null, false));
                    reorderService.SortByAttributes.Add(new SortAttribute("Property", null, false));
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            return reorderService;
        }

        public static StylerService CreateInstance(IStylerOptions options)
        {
            var stylerServiceInstance = new StylerService { Options = options };

            if (!String.IsNullOrEmpty(stylerServiceInstance.Options.NoNewLineElements))
            {
                stylerServiceInstance.NoNewLineElementsList = stylerServiceInstance.Options.NoNewLineElements.Split(',')
                    .Where(x => !String.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .ToList();
            }
            stylerServiceInstance.OrderRules = new AttributeOrderRules(options);

            stylerServiceInstance._elementProcessStatusStack.Clear();
            stylerServiceInstance._elementProcessStatusStack.Push(new ElementProcessStatus());

            return stylerServiceInstance;
        }

        private string UnescapeDocument(string source)
        {
            return _htmlReservedCharRestoreRegex.Replace(source, @"&$1;");
        }

        private string EscapeDocument(string source)
        {
            return _htmlReservedCharRegex.Replace(source, @"__amp__$1__scln__");
        }

        private string Format(string xamlSource)
        {
            StringBuilder output = new StringBuilder();

            using (var sourceReader = new StringReader(xamlSource))
            {
                // Not used
                // var settings = new XmlReaderSettings {IgnoreComments = false};
                using (XmlReader xmlReader = XmlReader.Create(sourceReader))
                {
                    while (xmlReader.Read())
                    {
                        switch (xmlReader.NodeType)
                        {
                            case XmlNodeType.Element:
                                UpdateParentElementProcessStatus(ContentTypeEnum.MIXED);

                                _elementProcessStatusStack.Push(
                                    new ElementProcessStatus
                                    {
                                        Name = xmlReader.Name,
                                        ContentType = ContentTypeEnum.NONE,
                                        IsMultlineStartTag = false,
                                        IsSelfClosingElement = false
                                    }
                                    );

                                ProcessElement(xmlReader, output);

                                if (_elementProcessStatusStack.Peek().IsSelfClosingElement)
                                {
                                    _elementProcessStatusStack.Pop();
                                }

                                break;

                            case XmlNodeType.Text:
                                UpdateParentElementProcessStatus(ContentTypeEnum.SINGLE_LINE_TEXT_ONLY);
                                ProcessTextNode(xmlReader, output);
                                break;

                            case XmlNodeType.ProcessingInstruction:
                                UpdateParentElementProcessStatus(ContentTypeEnum.MIXED);
                                ProcessInstruction(xmlReader, output);
                                break;

                            case XmlNodeType.Comment:
                                UpdateParentElementProcessStatus(ContentTypeEnum.MIXED);
                                ProcessComment(xmlReader, output);
                                break;
                            case XmlNodeType.CDATA:
                                ProcessCDATA(xmlReader, output);
                                break;
                            case XmlNodeType.Whitespace:
                                ProcessWhitespace(xmlReader, output);
                                break;

                            case XmlNodeType.EndElement:
                                ProcessEndElement(xmlReader, output);
                                _elementProcessStatusStack.Pop();
                                break;
                            case XmlNodeType.XmlDeclaration:
                                //ignoring xml declarations for Xamarin support
                                ProcessXMLRoot(xmlReader, output);
                                break;
                            //case XmlNodeType.CDATA:
                            //    break;
                            default:
                                Trace.WriteLine(
                                    $"Unprocessed NodeType: {xmlReader.NodeType} Name: {xmlReader.Name} Value: {xmlReader.Value}");
                                break;
                        }
                    }
                }
            }

            return output.ToString();
        }

        private void ProcessCDATA(XmlReader xmlReader, StringBuilder output)
        {
            UpdateParentElementProcessStatus(ContentTypeEnum.SINGLE_LINE_TEXT_ONLY);
            output
                .Append("<![CDATA[")
                .Append(xmlReader.Value)
                .Append("]]>");
        }

        /// <summary>
        /// Execute styling from string input
        /// </summary>
        /// <param name="xamlSource"></param>
        /// <returns></returns>
        public string ManipulateTreeAndFormatInput(string xamlSource)
        {
            Initialize();

            // parse XDocument
            var xDoc = XDocument.Parse(EscapeDocument(xamlSource), LoadOptions.PreserveWhitespace);

            // first, manipulate the tree; then, write it to a string
            return UnescapeDocument(Format(ManipulateTree(xDoc)));
        }

        private string ManipulateTree(XDocument xDoc)
        {
            var xmlDeclaration = xDoc.Declaration?.ToString() ?? string.Empty;
            var rootElement = xDoc.Root;

            if (rootElement != null && rootElement.HasElements)
            {
                // run through the elements and, one by one, handle them

                foreach (var element in rootElement.Elements())
                {
                    HandleNode(element);
                }
            }

            return xmlDeclaration + xDoc;
        }

        private void HandleNode(XNode node)
        {
            switch (node.NodeType)
            {
                case XmlNodeType.Element:
                    XElement element = node as XElement;

                    if (element != null && element.Nodes().Any())
                    {
                        // handle children first
                        foreach (var childNode in element.Nodes())
                        {
                            HandleNode(childNode);
                        }
                    }

                    if (element != null && element.HasElements)
                    {
                        foreach (var reorderService in ReorderServices)
                        {
                            reorderService.HandleElement(element);
                        }
                    }
                    break;
            }
        }




        private string GetIndentString(int depth)
        {
            if (depth < 0)
            {
                depth = 0;
            }

            if (Options.IndentWithTabs)
            {
                return new string('\t', depth);
            }

            return new string(' ', depth * Options.IndentSize);
        }

		private string TryTabifyIndentString(string str)
		{
			if (!Options.IndentWithTabs)
			{
				return str;
			}

			str = str.Replace("\t", new string(' ', Options.IndentSize));
			var spaces = new string(str.TakeWhile(char.IsWhiteSpace).ToArray());
			int numOfTabs = spaces.Length / Options.IndentSize;
			var leftOver = spaces.Length % Options.IndentSize;
			return new string('\t', numOfTabs) + new string(' ', leftOver);
		}

		private bool IsNoLineBreakElement(string elementName)
        {
            return NoNewLineElementsList.Contains<string>(elementName);
        }

        private void ProcessXMLRoot(XmlReader xmlReader, StringBuilder output)
        {
            output.Append("<?xml ");
            output.Append(xmlReader.Value.Trim());
            output.Append(" ?>");
        }

        private void ProcessComment(XmlReader xmlReader, StringBuilder output)
        {
            string currentIndentString = GetIndentString(xmlReader.Depth);
            string content = xmlReader.Value;

            if (!output.IsNewLine())
            {
                output.Append(Environment.NewLine);
            }

            if (content.Contains("<") && content.Contains(">"))
            {
                output.Append(currentIndentString);
                output.Append("<!--");
                if (content.Contains("\n"))
                {
                    output.Append(string.Join(Environment.NewLine, content.GetLines().Select(x => x.TrimEnd(' '))));
                    if (content.TrimEnd(' ').EndsWith("\n"))
                    {
                        output.Append(currentIndentString);
                    }
                }
                else
                    output.Append(content);

                output.Append("-->");
            }
            else if (content.Contains("\n"))
            {
                output
                    .Append(currentIndentString)
                    .Append("<!--");

                var contentIndentString = GetIndentString(xmlReader.Depth + 1);
                foreach (var line in content.Trim().GetLines())
                {
                    output
                        .Append(Environment.NewLine)
                        .Append(contentIndentString)
                        .Append(line.Trim());
                }

                output
                    .Append(Environment.NewLine)
                    .Append(currentIndentString)
                    .Append("-->");
            }
            else
            {
                output
                    .Append(currentIndentString)
                    .Append("<!--  ")
                    .Append(content.Trim())
                    .Append("  -->");
            }
        }

        private void ProcessElement(XmlReader xmlReader, StringBuilder output)
        {
            string currentIndentString = GetIndentString(xmlReader.Depth);
            string elementName = xmlReader.Name;

            if ("Run".Equals(elementName))
            {
                if (output.IsNewLine())
                {
                    // Shall not add extra whitespaces (including linefeeds) before <Run/>,
                    // because it will affect the rendering of <TextBlock><Run/><Run/></TextBlock>
                    output
                        .Append(currentIndentString)
                        .Append('<')
                        .Append(xmlReader.Name);
                }
                else
                {
                    output.Append('<');
                    output.Append(xmlReader.Name);
                }
            }
            else if (output.Length == 0 || output.IsNewLine())
            {
                output
                    .Append(currentIndentString)
                    .Append('<')
                    .Append(xmlReader.Name);
            }
            else
            {
                output
                    .Append(Environment.NewLine)
                    .Append(currentIndentString)
                    .Append('<')
                    .Append(xmlReader.Name);
            }

            bool isEmptyElement = xmlReader.IsEmptyElement;
            bool hasPutEndingBracketOnNewLine = false;
            var list = new List<AttributeInfo>(xmlReader.AttributeCount);

            if (xmlReader.HasAttributes)
            {
                while (xmlReader.MoveToNextAttribute())
                {
                    string attributeName = xmlReader.Name;
                    string attributeValue = xmlReader.Value;
                    AttributeOrderRule orderRule = OrderRules.GetRuleFor(attributeName);
                    list.Add(new AttributeInfo(attributeName, attributeValue, orderRule));
                }

                if (Options.OrderAttributesByName)
                    list.Sort();

                currentIndentString = GetIndentString(xmlReader.Depth);

                var noLineBreakInAttributes = (list.Count <= Options.AttributesTolerance) || IsNoLineBreakElement(elementName);
                // Root element?
                if (_elementProcessStatusStack.Count == 2)
                {
                    switch (Options.RootElementLineBreakRule)
                    {
                        case LineBreakRule.Default:
                            break;
                        case LineBreakRule.Always:
                            noLineBreakInAttributes = false;
                            break;
                        case LineBreakRule.Never:
                            noLineBreakInAttributes = true;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }

                // No need to break attributes
                if (noLineBreakInAttributes)
                {
                    foreach (var attrInfo in list)
                    {
                        output
                            .Append(' ')
                            .Append(attrInfo.ToSingleLineString());
                    }

                    _elementProcessStatusStack.Peek().IsMultlineStartTag = false;
                }

                // Need to break attributes
                else
                {
                    IList<String> attributeLines = new List<String>();

                    var currentLineBuffer = new StringBuilder();
                    int attributeCountInCurrentLineBuffer = 0;

                    AttributeInfo lastAttributeInfo = null;
                    foreach (AttributeInfo attrInfo in list)
                    {
                        // Attributes with markup extension, always put on new line
                        if (attrInfo.IsMarkupExtension && Options.FormatMarkupExtension)
                        {
                            string baseIndetationString;

                            if (!Options.KeepFirstAttributeOnSameLine)
                            {
                                baseIndetationString = GetIndentString(xmlReader.Depth);
                            }
                            else
                            {
                                baseIndetationString = GetIndentString(xmlReader.Depth - 1) +
                                                       string.Empty.PadLeft(elementName.Length + 2, ' ');
	                            baseIndetationString = TryTabifyIndentString(baseIndetationString);
                            }

                            string pendingAppend;

                            //Keep binding and / or x:bind on same line?
                            if ((attrInfo.Value.ToLower().Contains("x:bind ") && Options.KeepxBindOnSameLine) || Options.KeepBindingsOnSameLine)
                            {
                                pendingAppend = " " + attrInfo.ToSingleLineString();
                            }
                            else
                            {
                                pendingAppend = attrInfo.ToMultiLineString(baseIndetationString);
                            }

                            if (currentLineBuffer.Length > 0)
                            {
                                attributeLines.Add(currentLineBuffer.ToString());
                                currentLineBuffer.Length = 0;
                                attributeCountInCurrentLineBuffer = 0;
                            }

                            attributeLines.Add(pendingAppend);
                        }
                        else
                        {
                            string pendingAppend = attrInfo.ToSingleLineString();

                            bool isAttributeCharLengthExceeded =
                                (attributeCountInCurrentLineBuffer > 0 && Options.MaxAttributeCharatersPerLine > 0
                                 &&
                                 currentLineBuffer.Length + pendingAppend.Length > Options.MaxAttributeCharatersPerLine);

                            bool isAttributeCountExceeded =
                                (Options.MaxAttributesPerLine > 0 &&
                                 attributeCountInCurrentLineBuffer + 1 > Options.MaxAttributesPerLine);

                            bool isAttributeRuleGroupChanged = Options.PutAttributeOrderRuleGroupsOnSeparateLines
                                                               && lastAttributeInfo != null
                                                               && lastAttributeInfo.OrderRule.AttributeTokenType != attrInfo.OrderRule.AttributeTokenType;

                            if (isAttributeCharLengthExceeded || isAttributeCountExceeded || isAttributeRuleGroupChanged)
                            {
                                attributeLines.Add(currentLineBuffer.ToString());
                                currentLineBuffer.Length = 0;
                                attributeCountInCurrentLineBuffer = 0;
                            }

                            currentLineBuffer.AppendFormat("{0} ", pendingAppend);
                            attributeCountInCurrentLineBuffer++;
                        }

                        lastAttributeInfo = attrInfo;
                    }

                    if (currentLineBuffer.Length > 0)
                    {
                        attributeLines.Add(currentLineBuffer.ToString());
                    }

                    for (int i = 0; i < attributeLines.Count; i++)
                    {
                        if (0 == i && Options.KeepFirstAttributeOnSameLine)
                        {
                            output
                                .Append(' ')
                                .Append(attributeLines[i].Trim());

                            // Align subsequent attributes with first attribute
                            currentIndentString = GetIndentString(xmlReader.Depth - 1) +
                                                  String.Empty.PadLeft(elementName.Length + 2, ' ');
	                        currentIndentString = TryTabifyIndentString(currentIndentString);
                            continue;
                        }
                        output
                            .Append(Environment.NewLine)
                            .Append(currentIndentString)
                            .Append(attributeLines[i].Trim());
                    }

                    _elementProcessStatusStack.Peek().IsMultlineStartTag = true;
                }

                // Determine if to put ending bracket on new line
                if (Options.PutEndingBracketOnNewLine
                    && _elementProcessStatusStack.Peek().IsMultlineStartTag)
                {
                    output
                        .Append(Environment.NewLine)
                        .Append(currentIndentString);
                    hasPutEndingBracketOnNewLine = true;
                }
            }

            if (isEmptyElement)
            {
                if (hasPutEndingBracketOnNewLine == false && Options.SpaceBeforeClosingSlash)
                {
                    output.Append(' ');
                }
                output.Append("/>");

                _elementProcessStatusStack.Peek().IsSelfClosingElement = true;
            }
            else
            {
                output.Append(">");
            }
        }

        private void ProcessEndElement(XmlReader xmlReader, StringBuilder output)
        {
            // Shrink the current element, if it has no content.
            // E.g., <Element>  </Element> => <Element />
            if (ContentTypeEnum.NONE == _elementProcessStatusStack.Peek().ContentType
                && Options.RemoveEndingTagOfEmptyElement)
            {
                #region shrink element with no content

                output = output.TrimEnd(' ', '\t', '\r', '\n');

                int bracketIndex = output.LastIndexOf('>');
                output.Insert(bracketIndex, '/');

                if (output[bracketIndex - 1] != '\t' && output[bracketIndex - 1] != ' ' && Options.SpaceBeforeClosingSlash)
                {
                    output.Insert(bracketIndex, ' ');
                }

                #endregion shrink element with no content
            }
            else if (ContentTypeEnum.SINGLE_LINE_TEXT_ONLY == _elementProcessStatusStack.Peek().ContentType
                     && false == _elementProcessStatusStack.Peek().IsMultlineStartTag)
            {
                int bracketIndex = output.LastIndexOf('>');

                string text = output.Substring(bracketIndex + 1, output.Length - bracketIndex - 1).Trim();

                output.Length = bracketIndex + 1;
                output.Append(text).Append("</").Append(xmlReader.Name).Append(">");
            }
            else
            {
                string currentIndentString = GetIndentString(xmlReader.Depth);

                if (!output.IsNewLine())
                {
                    output.Append(Environment.NewLine);
                }

                output.Append(currentIndentString).Append("</").Append(xmlReader.Name).Append(">");
            }
        }

        private void ProcessInstruction(XmlReader xmlReader, StringBuilder output)
        {
            string currentIndentString = GetIndentString(xmlReader.Depth);

            if (!output.IsNewLine())
            {
                output.Append(Environment.NewLine);
            }

            output
                .Append(currentIndentString)
                .Append("<?Mapping ")
                .Append(xmlReader.Value)
                .Append(" ?>");
        }

        private void ProcessTextNode(XmlReader xmlReader, StringBuilder output)
        {
            string currentIndentString = GetIndentString(xmlReader.Depth);
            IEnumerable<String> textLines =
                xmlReader.Value.ToXmlEncodedString(ignoreCarrier: true).Trim().Split('\n').Where(
                    x => x.Trim().Length > 0).ToList();

            foreach (var line in textLines)
            {
                var trimmedLine = line.Trim();
                if (trimmedLine.Length > 0)
                {
                    output
                        .Append(Environment.NewLine)
                        .Append(currentIndentString)
                        .Append(trimmedLine);
                }
            }

            if (textLines.Count() > 1)
            {
                UpdateParentElementProcessStatus(ContentTypeEnum.MULTI_LINE_TEXT_ONLY);
            }
        }

        private void ProcessWhitespace(XmlReader xmlReader, StringBuilder output)
        {
            if (xmlReader.Value.Contains('\n'))
            {
                // For WhiteSpaces contain linefeed, trim all spaces/tab，
                // since the intent of this whitespace node is to break line,
                // and preserve the line feeds
                output.Append(xmlReader.Value
                    .Replace(" ", "")
                    .Replace("\t", "")
                    .Replace("\r", "")
                    .Replace("\n", Environment.NewLine));
            }
            else
            {
                // Preserve "pure" WhiteSpace between elements
                // e.g.,
                //   <TextBlock>
                //     <Run>A</Run> <Run>
                //      B
                //     </Run>
                //  </TextBlock>
                output.Append(xmlReader.Value);
            }
        }

        private void UpdateParentElementProcessStatus(ContentTypeEnum contentType)
        {
            ElementProcessStatus parentElementProcessStatus = _elementProcessStatusStack.Peek();

            parentElementProcessStatus.ContentType |= contentType;
        }
    }
}