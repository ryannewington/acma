﻿// -----------------------------------------------------------------------
// <copyright file="UniqueValueDeclaration.cs" company="Lithnet">
// Copyright (c) 2013
// </copyright>
// -----------------------------------------------------------------------

namespace Lithnet.Acma
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.Serialization;
    using System.Text.RegularExpressions;
    using Lithnet.Common.ObjectModel;
    using Lithnet.Fim.Core;
    using Lithnet.Fim.Transforms;
    using Microsoft.MetadirectoryServices;
    using Lithnet.Acma.DataModel;

    /// <summary>
    /// Defines a value declaration that contains parameters to guarantee uniqueness
    /// </summary>
    [DataContract(Name = "unique-value-declaration", Namespace = "http://lithnet.local/Lithnet.Acma/v1/")]
    public class UniqueValueDeclaration : UINotifyPropertyChanges, IExtensibleDataObject
    {
        /// <summary>
        /// Indicates if the declaration has been parsed
        /// </summary>
        private bool parsedDeclaration;

        /// <summary>
        /// Initializes a new instance of the UniqueValueDeclaration class
        /// </summary>
        /// <param name="declaration">The raw declaration string</param>
        public UniqueValueDeclaration(string declaration)
        {
            this.Initialize();
            this.Declaration = declaration;
        }

        /// <summary>
        /// Gets or sets the raw declaration string
        /// </summary>
        [DataMember(Name = "declaration")]
        public string Declaration { get; set; }

        /// <summary>
        /// Gets or sets the raw transform string
        /// </summary>
        [DataMember(Name = "transform-string")]
        public string TransformsString { get; set; }

        /// <summary>
        /// Gets or sets the serialization extension data object
        /// </summary>
        public ExtensionDataObject ExtensionData { get; set; }

        /// <summary>
        /// Gets the data type returned by the declaration
        /// </summary>
        public AttributeType DataType
        {
            get
            {
                return AttributeType.String;
            }
        }

        /// <summary>
        /// Gets or sets the list of attributes referenced in this object
        /// </summary>
        protected IList<AttributeDeclaration> AttributeDeclarations { get; set; }

        /// <summary>
        /// Gets or sets the list of variables referenced in this object
        /// </summary>
        protected IList<VariableDeclaration> VariableDeclarations { get; set; }

        /// <summary>
        /// Expands the declaration
        /// </summary>
        /// <param name="hologram">The object to use as the source of values during the expansion</param>
        /// <param name="isAttributeValueUnique">A function that can test if the value generated by the expansion is unique</param>
        /// <returns>Returns the expanded values</returns>
        public IList<object> Expand(MAObjectHologram hologram, Func<string, bool> isAttributeValueUnique)
        {
            if (!this.parsedDeclaration)
            {
                this.ParseDeclaration();
            }

            IList<Transform> transforms = ValueDeclaration.ExtractTransforms(this.TransformsString);
            List<object> values = new List<object>() { this.ExpandComplexDeclaration(hologram, isAttributeValueUnique) };

            return Transform.ExecuteTransformChain(transforms, values);
        }

        
            /// <summary>
        /// Updates the internal state when the declaration string changes
        /// </summary>
        internal void ParseDeclaration()
        {
            if (!this.parsedDeclaration)
            {
                ValueDeclarationParser p = new ValueDeclarationParser(this.Declaration);

                if (p.HasErrors)
                {
                    throw new InvalidDeclarationStringException(
                        string.Format(
                            "The declaration contained one or more errors: {0}\n{1}",
                            this.Declaration,
                            p.Errors.Select(u => u.ToString()).ToNewLineSeparatedString()));
                }

                this.AttributeDeclarations = p.AttributeDeclarations.ToList();

                if (this.AttributeDeclarations.Any(t => t.HasReferenceAttribute))
                {
                    throw new InvalidDeclarationStringException("A complex declaration cannot contain reference attributes");
                }

                this.VariableDeclarations = p.VariableDeclarations.ToList();

                int count = this.VariableDeclarations.Count(t => t.IsUniqueAllocationVariable);

                if (count == 0)
                {
                    throw new InvalidDeclarationStringException("The declaration string must contain a unique allocation variable (%o% or %n%)");
                }
                else if (count > 1)
                {
                    throw new InvalidDeclarationStringException("The declaration string must contain only a single unique allocation variable (%o% or %n%)");
                }

                this.parsedDeclaration = true;
            }
        }

        public SchemaAttributeUsage GetAttributeUsage(string parentPath, AcmaSchemaAttribute attribute)
        {
            if (this.AttributeDeclarations.Any(t => t.Attribute == attribute))
            {
                return new SchemaAttributeUsage("Value declaration", parentPath, this.Declaration);
            }

            return null;
        }
       
        /// <summary>
        /// Expands the attributes referenced in the declaration text
        /// </summary>
        /// <param name="hologram">The object containing the values to use in the expansion</param>
        /// <param name="isAttributeValueUnique">A function that can test if the value generated by the expansion is unique</param>
        /// <returns>The target attribute value with any references expanded out to their values</returns>
        private string ExpandComplexDeclaration(MAObjectHologram hologram, Func<string, bool> isAttributeValueUnique)
        {
            string constructedString = this.ExpandComplexDeclaration(hologram);

            if (isAttributeValueUnique != null)
            {
                constructedString = this.ProcessUniqueAllocations(hologram, constructedString, isAttributeValueUnique);
            }

            return constructedString;
        }

        /// <summary>
        /// Creates a value that is unique against the specified UniqueAllocationAttributes
        /// </summary>
        /// <param name="hologram">The object to construct the value for</param>
        /// <param name="constructedValue">The value under construction</param>
        /// <param name="isAttributeValueUnique">A function that can test if the value generated by the expansion is unique</param>
        /// <returns>A value that is unique against the specified UniqueAllocationAttributes</returns>
        private string ProcessUniqueAllocations(MAObjectHologram hologram, string constructedValue, Func<string, bool> isAttributeValueUnique)
        {
            string valueToTest = string.Empty;

            if (isAttributeValueUnique == null)
            {
                return constructedValue;
            }

            VariableDeclaration uniqueAllocator = this.VariableDeclarations.FirstOrDefault(t => t.IsUniqueAllocationVariable);
            if (uniqueAllocator == null)
            {
                throw new InvalidDeclarationStringException("The constructor requires a unique attribute allocation, but does not provide a %n% or %o% variable to use to make the value unique");
            }

            uniqueAllocator.ResetCounter();

            valueToTest = constructedValue;

            if (uniqueAllocator.VariableName == "o")
            {
                valueToTest = valueToTest.Replace(uniqueAllocator.Declaration, string.Empty);
            }
            else
            {
                IList<object> uniqueAllocatorValue = uniqueAllocator.Expand();

                valueToTest = valueToTest.Replace(uniqueAllocator.Declaration, uniqueAllocatorValue.First().ToSmartStringOrNull());
            }

            while (true)
            {
                if (!isAttributeValueUnique(valueToTest))
                {
                    IList<object> uniqueAllocatorValue = uniqueAllocator.Expand();

                    valueToTest = constructedValue.Replace(uniqueAllocator.Declaration, uniqueAllocatorValue.First().ToSmartStringOrNull());
                }
                else
                {
                    constructedValue = valueToTest;
                    break;
                }
            }

            return constructedValue;
        }

        /// <summary>
        /// Expands the attributes referenced in the declaration text
        /// </summary>
        /// <param name="hologram">The object containing the values to use in the expansion</param>
        /// <returns>The target attribute value with any references expanded out to their values</returns>
        private string ExpandComplexDeclaration(MAObjectHologram hologram)
        {
            string constructedString = this.Declaration;

            foreach (AttributeDeclaration declaration in this.AttributeDeclarations)
            {
                if (hologram == null)
                {
                    throw new ArgumentNullException("maObject");
                }

                IList<object> values = declaration.Expand(hologram);

                if (values.Count == 0)
                {
                    constructedString = constructedString.Replace(declaration.Declaration, string.Empty);
                }
                else if (values.Count > 1)
                {
                    throw new TooManyValuesException("The declaration string returned more than one attribute value which is unsupported for a complex value declaration", null);
                }
                else
                {
                    constructedString = constructedString.Replace(declaration.Declaration, values[0].ToSmartStringOrNull());
                }
            }

            constructedString = this.ExpandVariables(constructedString as string);

            return constructedString;
        }

        /// <summary>
        /// Expands the variables referenced in the declaration text
        /// </summary>
        /// <param name="existingExpansion">The current string value under expansion</param>
        /// <returns>The target attribute value with any references expanded out to their values</returns>
        private string ExpandVariables(string existingExpansion)
        {
            string constructedString = existingExpansion;

            foreach (VariableDeclaration declaration in this.VariableDeclarations.Where(t => !t.IsUniqueAllocationVariable))
            {
                IList<object> values = declaration.Expand();

                if (values.Count == 0)
                {
                    constructedString = constructedString.Replace(declaration.Declaration, string.Empty);
                }
                else if (values.Count > 1)
                {
                    throw new TooManyValuesException("The declaration string returned more than one variable value which is unsupported for a complex value declaration", null);
                }
                else
                {
                    constructedString = constructedString.Replace(declaration.Declaration, values[0].ToSmartStringOrNull());
                }
            }

            return constructedString;
        }

        /// <summary>
        /// Occurs just prior to the object being deserialized
        /// </summary>
        /// <param name="context">The serialization context</param>
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            this.Initialize();
        }

        /// <summary>
        /// Occurs after the object has been deserialized
        /// </summary>
        /// <param name="context">The serialization context</param>
        [OnDeserialized]
        private void OnDeserialized(StreamingContext context)
        {
        }

        /// <summary>
        /// Initializes the object
        /// </summary>
        private void Initialize()
        {
            this.AttributeDeclarations = new List<AttributeDeclaration>();
            this.VariableDeclarations = new List<VariableDeclaration>();
        }
    }
}
