﻿using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml;

namespace FatAntelope.Writers
{
    /// <summary>
    /// An XML diffgram writer for the microsoft Xml-Document-Transform (xdt) format.
    /// </summary>
    /// <remarks>
    /// This implementation makes some assumptions about common XML in the config file, and is a little hacky.
    /// May not produce the best result with placement of xdt:Transform and xdt:Locator attributes.
    /// </remarks>
    public class XdtDiffWriter : BaseDiffWriter
    {
        #region Helper Classes
        
        /// <summary>
        /// Store counts of updated, inserted, deleted and unchanged child XML nodes
        /// </summary>
        private class Counts
        {
            public int Updates { get; set; }
            public int Inserts { get; set; }
            public int Deletes { get; set; }
            public int Unchanged { get; set; }

            public bool IsInsertsOnly(bool ignoreUnchanged = false)
            {
                return Inserts > 0
                    && Updates == 0
                    && Deletes == 0
                    && (Unchanged == 0 || ignoreUnchanged);
            }

            public bool IsUpdatesOnly(bool ignoreUnchanged = false)
            {
                return Inserts == 0
                    && Updates > 0
                    && Deletes == 0
                    && (Unchanged == 0 || ignoreUnchanged);
            }

            public bool IsDeletesOnly(bool ignoreUnchanged = false)
            {
                return Inserts == 0
                    && Updates == 0
                    && Deletes > 0
                    && (Unchanged == 0 || ignoreUnchanged);
            }

            public bool HasAny()
            {
                return Updates + Inserts + Deletes + Unchanged > 0;
            }

            public bool HasChanges()
            {
                return Updates + Inserts + Deletes > 0;
            }

            public int TotalChanges()
            {
                return Updates + Inserts + Deletes;
            }

            public int Total()
            {
                return Updates + Inserts + Deletes + Unchanged;
            }
        }

        /// <summary>
        /// Stores unique trait/s for an element
        /// </summary>
        private class Trait
        {
            public int Index { get; set; }
            public XNode Attribute { get; set; }
            public bool UniqueInBoth { get; set; }

            public Trait(int index = 0, XNode attribute = null, bool uniqueInBoth = false)
            {
                Index = -1;
                Index = index;
                Attribute = attribute;
                UniqueInBoth = uniqueInBoth;
            }


        }

        #endregion

        public XdtDiffWriter(AppSettingJson settings)
        {
            this.Settings = settings;
        }

        /// <summary>
        /// Types of xdt transforms
        /// </summary>
        private enum TransformType
        {
            None = 0,
            RemoveAttributes = 1,
            SetAttributes = 2,
            RemoveAndSetAttributes = 3, // HACK: non-standard transform to get around SetAttributes not supporting remove
            Insert = 4,
            InsertBefore = 5,
            InsertAfter = 6,
            Remove = 7,
            RemoveAll = 8,
            Replace = 9
        }

        private const string XmlNamespace = "xmlns";
        private const string XmlNamespaceUri = "http://www.w3.org/2000/xmlns/";
        private const string XdtNamespace = "http://schemas.microsoft.com/XML-Document-Transform";
        private const string XdtPrefix = "xdt";
        private const string XdtTransform = "Transform";
        private const string XdtLocator = "Locator";
        private const string XdtMatch = "Match({0})";
        private const string XdtXPath = "XPath({0})";
        private const string XdtCondition = "Condition({0})";
        private const string XdtSetAttributes = "SetAttributes({0})";
        private const string XdtRemoveAttributes = "RemoveAttributes({0})";
        private const string XdtInsertBefore = "InsertBefore({0}/{1}{2})";
        private const string XdtInsertAfter = "InsertAfter({0}/{1}{2})";
        private const string XPathPredicate = "[({0}='{1}')]";
        private const string XPathIndexPredicate = "[{0}]";


        public AppSettingJson Settings { get; set; }

        /// <summary>
        /// Write the diff / patch to the given file
        /// </summary>
        public override void WriteDiff(XTree tree, string file)
        {
            var doc = GetDiff(tree);
            doc.Save(file);
        }

        /// <summary>
        /// Get the diff patch for the tree
        /// </summary>
        public XmlDocument GetDiff(XTree tree)
        {
            var doc = new XmlDocument();
            var declaration = doc.CreateXmlDeclaration("1.0", "utf-8", null);
            doc.AppendChild(declaration);

            var root = WriteElement(tree.Root.Matching, tree.Root, doc, string.Empty, 1);

            var attr = doc.CreateAttribute(XmlNamespace, XdtPrefix, XmlNamespaceUri);
            attr.Value = XdtNamespace;
            root.Attributes.Append(attr);

            // remove //configuration/runtime/assemblyBinding
            foreach (var removeNodePath in this.Settings.RemoveNodePath)
            {
                var removeNode =
                    root.SelectSingleNode(removeNodePath);
                if (removeNode != null && removeNode.ChildNodes.Count > 0)
                {
                    removeNode.RemoveAll();
                }
            }

            return doc;
        }

        /// <summary>
        /// Append the changed element to the new config transform. The given element may have been updated, inserted or deleted.
        /// </summary>
        private XmlNode WriteElement(XNode oldElement, XNode newElement, XmlNode target, string path, int index, bool forceInsert = false)
        {
            XmlNode element = null;
            var transform = GetTransformType(oldElement, newElement);

            if (this.Settings.ResetNodePath.Contains(path))
            {
                if (oldElement?.Children.FirstOrDefault() != null)
                {
                    element = AddElement(target, oldElement);
                    AddTransform(element, TransformType.RemoveAll.ToString());

                    oldElement.XmlNode.RemoveAll();

                    transform = TransformType.Insert;
                }
            }

            // Insert
            if (transform == TransformType.Insert)  
            {
                element = CopyNode(newElement, target);
                var insertTransform = GetInsertTransform(newElement, path, index);
                AddTransform(element, insertTransform);
                return element;
            }

            // Get the uniquely identifiable trait for the element, in both old and new trees
            var newTrait = GetUniqueTrait(newElement);
            var oldTrait = GetUniqueTrait(oldElement);

            // Replace
            if (transform == TransformType.Replace)  
            {
                element = CopyNode(newElement, target);
                AddTransform(element, transform.ToString());
                AddLocator(element, oldElement, oldTrait, false, transform);
                return element;
            }
            element = AddElement(target, oldElement);
            AddLocator(element, oldElement, oldTrait, true, transform);

            // Remove
            if (transform == TransformType.Remove)  
            {
                AddTransform(element, TransformType.Remove.ToString());
                return element;
            }

            // RemoveAttributes
            else if (transform == TransformType.RemoveAttributes || transform == TransformType.RemoveAndSetAttributes) 
            {
                var builder = new StringBuilder();
                var first = true;
                foreach (var attr in oldElement.Attributes)
                {
                    if (attr.Match == MatchType.NoMatch)
                    { 
                        builder.Append((first ? string.Empty : ",") + attr.XmlNode.Name);
                        first = false;
                    }
                }
                AddTransform(element, string.Format(XdtRemoveAttributes, builder.ToString()));

                if (transform == TransformType.RemoveAndSetAttributes)   // RemoveAndSetAttributes
                {
                    var element2 = AddElement(target, oldElement);
                    AddLocator(element2, oldElement, oldTrait, true, transform);
                    var attributeList = CopyAttributes(newElement, element2);
                    AddTransform(element2, string.Format(XdtSetAttributes, attributeList));
                }
            }

            // SetAttributes
            else if (transform == TransformType.SetAttributes)  
            {
                var attributeList = CopyAttributes(newElement, element);
                AddTransform(element, string.Format(XdtSetAttributes, attributeList));
            }

            // Before processing child elements, update the trait attribute, as it could have 
            //  changed after a transformation was applied to this node.
            if (oldTrait != null && oldTrait.Attribute != null && oldTrait.Attribute.Match != MatchType.Match)
            {
                oldTrait.Attribute = (newTrait != null && newTrait.UniqueInBoth)
                    ? newTrait.Attribute
                    : null;
            }

            // Calculate xpath to current element for use in child node changes
            path = GetPath(path, oldElement, oldTrait);

            // Process 'changed' child elements first
            for (var i = 0; i < newElement.Elements.Length; i++ )
            {
                var child = newElement.Elements[i];
                if (child.Match == MatchType.Change)
                {
                    //WriteElement(child.Matching, child, element, path, i);
                    WriteElement(child.Matching, child, element, path, i, forceInsert);
                }
            }

            // Process 'inserted' and 'removed' child elements together in reverse
            var newElements = newElement.Elements;
            var oldElements = oldElement.Elements;
            var max = newElements.Length > oldElements.Length
                ? newElements.Length
                : oldElements.Length;

            for (var i = max - 1; i >= 0; i--)
            {
                // Remove child element
                if(oldElements.Length > i)
                {
                    var remove = oldElements[i];
                    if (remove.Match == MatchType.NoMatch)
                        WriteElement(remove, null, element, path, 0);
                }

                // Insert child element
                if (newElements.Length > i)
                {
                    var insert = newElements[i];
                    if (insert.Match == MatchType.NoMatch)
                        WriteElement(insert.Matching, insert, element, path, i);
                }
            }

            return element;
        }

        /// <summary>
        /// Copy all inserted or updated attributes to the given element. 
        /// </summary>
        private string CopyAttributes(XNode node, XmlNode parent)
        {
            var builder = new StringBuilder();
            var first = true;
            foreach (var attr in node.Attributes)
            {
                if (attr.Match == MatchType.Change || attr.Match == MatchType.NoMatch)
                {
                    var attribute = CopyAttribute(attr, parent);
                    builder.Append((first ? string.Empty : ",") + attr.XmlNode.Name);
                    first = false;
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Copy an attribute to the given element. 
        /// </summary>
        private XmlNode CopyAttribute(XNode node, XmlNode parent)
        {
            var child = parent.OwnerDocument.ImportNode(node.XmlNode, true);
            parent.Attributes.Append(child as XmlAttribute);

            return child;
        }

        /// <summary>
        /// Copy an element or text node to the given element. 
        /// </summary>
        private XmlNode CopyNode(XNode node, XmlNode parent)
        {
            var child = (parent is XmlDocument)
                ? (parent as XmlDocument).ImportNode(node.XmlNode, true)
                : parent.OwnerDocument.ImportNode(node.XmlNode, true);

            parent.AppendChild(child);

            return child;
        }

        /// <summary>
        /// Append a new attribute to the given element. 
        /// </summary>
        private XmlAttribute AddAttribute(XmlNode parent, string prefix, string name, string namespaceUri, string value)
        {
            var attr = parent.OwnerDocument.CreateAttribute(prefix, name, namespaceUri);
            attr.Value = value;

            return parent.Attributes.Append(attr);
        }

        /// <summary>
        /// Append a new element with the given name. 
        /// </summary>
        private XmlElement AddElement(XmlNode parent, XNode element)
        {
            var elem = (parent.OwnerDocument ?? (XmlDocument)parent).CreateElement(element.XmlNode.Name);
            
            // Copy over namespace declaration attributes
            foreach (var attribute in element.Attributes)
            {
                var name = attribute.Name.ToLowerInvariant();
                if (name == XmlNamespace || name.StartsWith(XmlNamespace + ":"))
                    CopyAttribute(attribute, elem);
            }
            
            parent.AppendChild(elem);
            return elem;
        }

        /// <summary>
        /// Add the xdt:Locator attribute to the given element when necessary. 
        /// Will use the Match(attribute) option instead of a Condition when possible.
        /// </summary>
        private XmlAttribute AddLocator(XmlNode parent, XNode element, Trait trait, bool addAttribute, TransformType transform)
        {
            if (trait != null)
            {
                if(trait.Attribute != null)
                { 
                    var attribute = trait.Attribute;
                    if (attribute.Match == MatchType.Match)
                    {
                        if (addAttribute)
                            CopyAttribute(attribute, parent);

                        return AddAttribute(parent, XdtPrefix, XdtLocator, XdtNamespace, string.Format(XdtMatch, attribute.XmlNode.Name));
                    }

                    if ((transform != TransformType.SetAttributes 
                        && transform != TransformType.RemoveAttributes 
                        && transform != TransformType.RemoveAndSetAttributes)
                        || !HasChildChanges(element))
                        return AddAttribute(parent, XdtPrefix, XdtLocator, XdtNamespace, string.Format(XdtCondition, BuildPredicate(attribute.XmlNode)));
                }

                return AddAttribute(parent, XdtPrefix, XdtLocator, XdtNamespace, string.Format(XdtCondition, trait.Index));

            }
            return null;
        }

        private bool HasChildChanges(XNode element)
        {
            foreach(var child in element.Elements)
            {
                if(child.Match != MatchType.Match)
                    return true;
            }

            if(element.Matching != null)
            { 
                foreach (var child in element.Matching.Elements)
                {
                    if (child.Match != MatchType.Match)
                        return true;
                }
            }

            return false;
        }

        private string BuildPredicate(XmlNode node)
        {
            return string.Format(XPathPredicate, (node.NodeType == XmlNodeType.Attribute ? "@" : string.Empty) + node.Name, node.Value);
        }

        /// <summary>
        /// Add the xdt:Transform attribute to the given element
        /// </summary>
        private XmlAttribute AddTransform(XmlNode element, string value)
        {
            return AddAttribute(element, XdtPrefix, XdtTransform, XdtNamespace, value);
        }

        /// <summary>
        /// Construct the xdt:Transform value for element insertion
        /// </summary>
        private string GetInsertTransform(XNode element, string path, int index)
        {
            var parent = element.Parent;

            // If only element, or last element, then use 'Insert'
            // edited  by g-neochang
            //if (parent == null || parent.Elements.Length < 2 || parent.Elements.Length == index + 1)
            //    return TransformType.Insert.ToString();
            return TransformType.Insert.ToString();

            // Can we insert before the next child element
            var next = parent.Elements[index + 1];
            var trait = GetUniqueTrait(next);
            if (trait == null)
                return string.Format(XdtInsertBefore, path, next.Name, string.Empty);

            if (trait.Attribute != null)
                return string.Format(XdtInsertBefore, path, next.Name, BuildPredicate(trait.Attribute.XmlNode));

            // Otherwise can we insert after the previous child element
            var previous = GetPrevious(parent, index);

            // No previous element, just insert at the begining of the children
            if (previous == null)
                return string.Format(XdtInsertBefore, path, "*", "[1]");

            trait = GetUniqueTrait(previous);
            if (trait == null)
                return string.Format(XdtInsertAfter, path, previous.Name, string.Empty);

            if (trait.Attribute != null)
                return string.Format(XdtInsertAfter, path, previous.Name, BuildPredicate(trait.Attribute.XmlNode));

            if (trait.Index > 0)
                return string.Format(XdtInsertAfter, path, previous.Name, string.Format(XPathIndexPredicate, trait.Index));

            return TransformType.Insert.ToString();
        }

        private XNode GetPrevious(XNode parent, int index)
        {
            for (var i = index - 1; i >= 0; i--)
            {
                var previous = parent.Elements[i];
                if (previous.Match != MatchType.NoMatch)
                    return previous;
            }

            return null;
        }

        /// <summary>
        /// Calculate unique traits for the given element - unique index and/or attribute
        /// </summary>
        private Trait GetUniqueTrait(XNode element)
        {
            if (element != null && element.Parent != null)
            {
                var parent = element.Parent;
                var duplicates = new List<XNode>();
                var pairedDuplicates = new List<XNode>();
                var index = -1;

                // Check for siblings with the same name
                foreach(var child in parent.Elements)
                {
                    if (child.Name == element.Name)
                    {
                        duplicates.Add(child);
                        if (child == element)
                            index = duplicates.Count;
                    }
                }

                // Check for siblings in the other tree with the same name
                if(parent.Matching != null)
                {
                    foreach (var child in parent.Matching.Elements)
                    {
                        if (child.Name == element.Name && child.Matching != element)
                            pairedDuplicates.Add(child);
                    }
                }

                // Mulitple elements with the same name 
                if (duplicates.Count > 1)
                {
                    XNode poorAttribute = null;
                    bool uniqueInBoth = false;

                    // try and find unique attribute
                    foreach (var attribute in element.Attributes)
                    {
                        if(IsUnique(duplicates, attribute))
                        {
                            uniqueInBoth = IsUnique(pairedDuplicates, attribute);

                            if (attribute.Match != MatchType.Match)
                                poorAttribute = poorAttribute ?? attribute;
                            else
                                return new Trait(index, attribute, uniqueInBoth);
                        }
                    }

                    return new Trait(index, poorAttribute, uniqueInBoth);
                }
            }

            // No duplicates, can simply use path / name
            return null;
        }

        /// <summary>
        /// Determine if the attribute has a unique value amongst its element peers (having the same element name)
        /// </summary>
        private bool IsUnique(List<XNode> duplicates, XNode attribute)
        {
            var value = attribute.XmlNode.Value;

            foreach (var child in duplicates)
            {
                foreach (var childAttr in child.Attributes)
                {
                    if (childAttr.Name == attribute.Name)
                    {
                        if (value == childAttr.XmlNode.Value && childAttr != attribute)
                            return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Calculate the type of xdt:Transform attribute to write with this element 
        /// </summary>
        private TransformType GetTransformType(XNode oldElement, XNode newElement)
        {
            if (oldElement != null && oldElement.Match == MatchType.NoMatch)
                return TransformType.Remove;

            if (newElement != null && newElement.Match == MatchType.NoMatch)
                return TransformType.Insert;

            // if text nodes have changed, then we must replace
            var texts = GetCounts(oldElement.Texts, newElement.Texts);
            if (texts.HasChanges())
                return TransformType.Replace;

            var attributes = GetCounts(oldElement.Attributes, newElement.Attributes);
            var elements = GetCounts(oldElement.Elements, newElement.Elements);

            // If mostly only element inserts & deletes, then replace
            if (elements.Deletes + elements.Inserts > 0 
                && elements.Unchanged + elements.Updates == 0 
                && attributes.Unchanged < elements.TotalChanges())
                return TransformType.Replace;

            // If has attribute changes
            if(attributes.HasChanges())
            {
                // If all attributes have changed, replace
                if (attributes.Unchanged == 0 && elements.Total() == 0 && texts.Total() == 0)
                    return TransformType.Replace;
                
                // If only attribute deletes, mark attributes for removal
                if(attributes.IsDeletesOnly(true))
                    return TransformType.RemoveAttributes;
                    
                // If both removing and changing / inserting some attributes.
                if(attributes.Deletes > 0)
                    return TransformType.RemoveAndSetAttributes;

                return TransformType.SetAttributes;
            }

            return TransformType.None;
        }

        /// <summary>
        /// Calculate the inserted, updated, deleted and unchanged counts for the given nodes
        /// </summary>
        private Counts GetCounts(XNode[] original, XNode[] updated)
        {
            var counts = new Counts();

            // Check for attribute changes
            foreach (var child in updated)
            {
                if (child.Match == MatchType.Change)
                    counts.Updates++;

                if (child.Match == MatchType.NoMatch)
                    counts.Inserts++;

                if (child.Match == MatchType.Match)
                    counts.Unchanged++;

            }
            foreach (var child in original)
            {
                if (child.Match == MatchType.NoMatch)
                    counts.Deletes++;
            }

            return counts;
        }

        /// <summary>
        /// Get the absolute XPath for the given element
        /// </summary>
        private string GetPath(string path, XNode element, Trait trait)
        {
            var newPath = path + "/" + element.XmlNode.Name;
            if(trait != null)
            { 
                if(trait.Attribute != null)
                    return newPath + BuildPredicate(trait.Attribute.XmlNode);

                if(trait.Index >= 0)
                    return newPath + string.Format(XPathIndexPredicate, trait.Index);
            }

            return newPath;
        }
    }
}
