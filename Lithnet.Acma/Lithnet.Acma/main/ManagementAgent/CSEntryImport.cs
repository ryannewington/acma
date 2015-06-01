﻿// -----------------------------------------------------------------------
// <copyright file="CSEntryImport.cs" company="Ryan Newington">
// Copyright (c) 2013 Ryan Newington
// </copyright>
// -----------------------------------------------------------------------

namespace Lithnet.Acma
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Linq;
    using Microsoft.MetadirectoryServices;
    using Lithnet.Acma.DataModel;
    using Lithnet.Fim.Core;

    /// <summary>
    /// Provides methods for importing connector space objects to the FIM Sync Service
    /// </summary>
    public static class CSEntryImport
    {
        private static Dictionary<AcmaSchemaObjectClass, IEnumerable<AcmaSchemaAttribute>> requestedTypes;

        /// <summary>
        /// Constructs a CSEntryChange object for the specified MAObject or MA_Delta_Object
        /// </summary>
        /// <param name="maObject">The MAObject or MA_Delta_Object to construct the CSEntry for</param>
        /// <param name="types">The schema types required for the current import operation</param>
        /// <returns>A CSEntryChange object representing the specified MAObject</returns>
        public static CSEntryChange GetCSEntry(MAObjectHologram maObject, Dictionary<AcmaSchemaObjectClass, IEnumerable<AcmaSchemaAttribute>> requestedTypes)
        {
            CSEntryImport.requestedTypes = requestedTypes;
            CSEntryChange csentry = null;

            try
            {
                if (maObject.DeltaChangeType != "delete")
                {
                    // delete operations do not have the object class remembered, so we cannot do this check

                    if (!requestedTypes.Any(t => t.Key == maObject.ObjectClass))
                    {
                        // This object type is not required for import
                        return null;
                    }

                    maObject.PreLoadAVPs(requestedTypes[maObject.ObjectClass]);
                }
                else
                {
                    // ECMA2 connectors without a non-DN LDAP type must provide the object class when performing a delete
                    // As we don't have this in the change log, we cannot delta import deletes. The capabilities of this
                    // MA are marked as not requiring delete confirmation. 
                    // Microsoft have agreed to fix this in case REG:115031112499499 in a future release
                    // Until then, we don't import the deletes during a delta
                    return null;
                }

                csentry = GetCSEntry(maObject);
                // maObject.ClearDeltaEntry();

                if (csentry.ErrorCodeImport == MAImportError.Success)
                {
                    MAStatistics.AddImportOperation();
                }
                else
                {
                    MAStatistics.AddImportError();
                }
            }
            catch (Exception ex)
            {
                MAStatistics.AddImportError();

                if (csentry == null)
                {
                    csentry = CSEntryChange.Create();
                }

                if (string.IsNullOrWhiteSpace(csentry.DN))
                {
                    csentry.ErrorCodeImport = MAImportError.ImportErrorCustomStopRun;
                }
                else
                {
                    csentry.ErrorCodeImport = MAImportError.ImportErrorCustomContinueRun;
                }

                csentry.ErrorName = ex.Message;
                csentry.ErrorDetail = ex.StackTrace;
            }

            return csentry;
        }

        /// <summary>
        /// Creates a CSEntryChange for the specified MAObjectHologram
        /// </summary>
        /// <param name="maObject">The MAObjectHologram to construct the CSEntry for</param>
        /// <returns>The newly created CSEntryChange</returns>
        private static CSEntryChange GetCSEntry(MAObjectHologram maObject)
        {
            CSEntryChange csentry = CSEntryChange.Create();

            switch (maObject.DeltaChangeType)
            {
                case null:
                case "add":
                    csentry.ObjectModificationType = ObjectModificationType.Add;
                    csentry.ObjectType = maObject.ObjectClass.Name;
                    csentry.DN = maObject.Id.ToString();
                    csentry.AttributeChanges.Add(AttributeChange.CreateAttributeAdd("objectId", maObject.Id.ToString()));
                    GetObject(maObject, csentry);
                    break;

                case "delete":
                    csentry.ObjectModificationType = ObjectModificationType.Delete;
                    //csentry.AnchorAttributes.Add(AnchorAttribute.Create("objectId", maObject.Id.ToString()));
                    csentry.DN = maObject.Id.ToString();
                    break;

                case "modify":
                case "attrmodify":
                    csentry.ObjectModificationType = ObjectModificationType.Replace;
                    csentry.ObjectType = maObject.ObjectClass.Name;
                    csentry.DN = maObject.Id.ToString();
                    csentry.AnchorAttributes.Add(AnchorAttribute.Create("objectId", maObject.Id.ToString()));
                    GetObject(maObject, csentry);
                    break;

                default:
                    throw new InvalidOperationException(string.Format("The change type {0} is unknown", maObject.DeltaChangeType));
            }

            return csentry;
        }

        /// <summary>
        /// Contributes to a CSEntryChange for the specified MA_Delta_Object by populating newly added object and its attributes
        /// </summary>
        /// <param name="maObject">The MAObject to construct the CSEntry for</param>
        /// <param name="csentry">The CSEntryChange object to contribute to</param>
        private static void GetObject(MAObjectHologram maObject, CSEntryChange csentry)
        {
            try
            {
                // maObject.PreLoadAVPs();

                foreach (AcmaSchemaAttribute maAttribute in CSEntryImport.requestedTypes[maObject.ObjectClass].Where(t => !t.Name.Equals("objectId", StringComparison.CurrentCultureIgnoreCase)))
                {
                    List<object> values = new List<object>();
                    AttributeValues dbvalues = maObject.GetAttributeValues(maAttribute);
                    ValidateImportSafety(dbvalues, maObject.ObjectClass);
                    values.AddRange(dbvalues.Where(t => !t.IsNull).Select(t => t.Value));

                    if (values.Count > 0)
                    {
                        if (maAttribute.Type == ExtendedAttributeType.Reference)
                        {
                            csentry.AttributeChanges.Add(AttributeChange.CreateAttributeAdd(maAttribute.Name, values.Select(t => t.ToString()).ToList<object>()));
                        }
                        else if (maAttribute.Type == Fim.Core.ExtendedAttributeType.DateTime)
                        {
                            csentry.AttributeChanges.Add(AttributeChange.CreateAttributeAdd(maAttribute.Name, values.Select(t => ((DateTime)t).ToFimServiceString()).ToList<object>()));
                        }
                        else
                        {
                            csentry.AttributeChanges.Add(AttributeChange.CreateAttributeAdd(maAttribute.Name, values));
                        }
                    }
                }
            }
            catch (SafetyRuleViolationException ex)
            {
                csentry.ErrorCodeImport = MAImportError.ImportErrorCustomContinueRun;
                csentry.ErrorName = ex.Message;
                csentry.ErrorDetail = ex.Message + "\n" + ex.StackTrace;
            }
        }

        /// <summary>
        /// Validates that the specified attribute value meets the import safety requirements
        /// </summary>
        /// <param name="value">The value to validate</param>
        private static void ValidateImportSafety(AttributeValue value, AcmaSchemaObjectClass objectClass)
        {
            foreach (SafetyRule rule in objectClass.Mappings.First(t => t.ObjectClass == objectClass).SafetyRules)
            {
                rule.Validate(value);
            }
        }

        /// <summary>
        /// Validates that the specified attribute values meets the import safety requirements
        /// </summary>
        /// <param name="values">The values to validate</param>
        private static void ValidateImportSafety(AttributeValues values, AcmaSchemaObjectClass objectClass)
        {
            foreach (SafetyRule rule in objectClass.Mappings.First(t => t.ObjectClass == objectClass).SafetyRules)
            {
                rule.Validate(values);
            }
        }
    }
}