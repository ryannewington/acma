﻿// -----------------------------------------------------------------------
// <copyright file="CSEntryExport.cs" company="Ryan Newington">
// Copyright (c) 2013 Ryan Newington
// </copyright>
// -----------------------------------------------------------------------

namespace Lithnet.Acma
{
    using System;
    using System.Collections.Generic;
    using System.Data;
    using System.Data.SqlClient;
    using System.Linq;
    using Lithnet.Logging;
    using Microsoft.MetadirectoryServices;
    using Lithnet.MetadirectoryServices;
    using Lithnet.Acma.DataModel;
    using System.Transactions;

    /// <summary>
    /// Provides methods for exporting connector space objects to the database
    /// </summary>
    public static class CSEntryExport
    {
        /// <summary>
        /// Exports a single entry
        /// </summary>
        /// <param name="csentry">The entry to export</param>
        /// <param name="dbc">The DBDataContext for the current thread</param>
        /// <param name="referenceRetryRequired">A value indicating whether one or more referenced objects were not found</param>
        /// <returns>A list of anchor attributes if the object was added to the database, otherwise returns an empty list</returns>
        public static IList<AttributeChange> PutExportEntry(CSEntryChange csentry, MADataContext dbc, out bool referenceRetryRequired)
        {
            IList<AttributeChange> anchorchanges = new List<AttributeChange>();
            referenceRetryRequired = false;
            bool hasTransaction = Transaction.Current != null;

            try
            {
                using (TransactionScope transaction = new TransactionScope(TransactionScopeOption.Required, TransactionManager.MaximumTimeout))
                {
                    if (!hasTransaction)
                    {
                        dbc.SharedHologramSqlConnection.EnlistTransaction(Transaction.Current);
                    }

                    MAStatistics.StartTransaction();

                    switch (csentry.ObjectModificationType)
                    {
                        case ObjectModificationType.Add:
                            anchorchanges = CSEntryExport.PerformCSEntryExportAdd(csentry, dbc, out referenceRetryRequired);
                            break;

                        case ObjectModificationType.Delete:
                            CSEntryExport.PerformCSEntryExportDelete(csentry, dbc);
                            break;

                        case ObjectModificationType.None:
                            break;

                        case ObjectModificationType.Update:
                            CSEntryExport.PerformCSEntryExportUpdate(csentry, dbc, out referenceRetryRequired);
                            break;

                        case ObjectModificationType.Replace:
                            CSEntryExport.PerformCSEntryExportReplace(csentry, dbc, out referenceRetryRequired);
                            break;

                        default:
                        case ObjectModificationType.Unconfigured:
                            throw new UnknownOrUnsupportedModificationTypeException(csentry.ObjectModificationType);
                    }

                    transaction.Complete();
                    MAStatistics.CompleteTransaction();
                }
            }
            catch (Exception ex)
            {
                Logger.WriteException(ex);
                MAStatistics.RollBackTransaction();
                throw;
            }

            MAStatistics.AddExportOperation(csentry.ObjectModificationType);

            return anchorchanges;
        }

        /// <summary>
        /// Adds a new object to the database
        /// </summary>
        /// <param name="csentry">The CSEntryChange containing the new object and its attributes</param>
        /// <param name="dc">The DBDataContext in use on this thread</param>
        /// <param name="referenceRetryRequired">A value indicating whether a reference update failed due to a missing object and needs to be retried after all other CSEntryChanges have been processed</param>
        /// <returns>A list of anchor attributes for the new object</returns>
        private static IList<AttributeChange> PerformCSEntryExportAdd(CSEntryChange csentry, MADataContext dc, out bool referenceRetryRequired)
        {
            AcmaSchemaObjectClass objectClass = ActiveConfig.DB.GetObjectClass(csentry.ObjectType);
            bool isUndeleting = false;
            MAObjectHologram hologram;
            Guid guidFromDn = new Guid(csentry.DN);

            if (objectClass.IsShadowObject)
            {
                hologram = CSEntryExport.ExportShadowObject(csentry, dc);
            }
            else
            {
                hologram = GetResurrectionObject(csentry, dc, objectClass);

                if (hologram == null)
                {
                    hologram = dc.CreateMAObject(guidFromDn, csentry.ObjectType);
                }
                else
                {
                    Logger.WriteLine("Resurrecting object with ID: " + hologram.ObjectID.ToString());
                    isUndeleting = true;

                    if (hologram.ObjectID != guidFromDn)
                    {
                        Logger.WriteLine("Re-anchoring object with new ID: " + csentry.DN);
                        dc.ChangeMAObjectId(hologram.ObjectID, guidFromDn, true);
                        hologram = dc.GetMAObject(guidFromDn, objectClass);
                    }

                    csentry = CSEntryChangeExtensions.ConvertCSEntryChangeAddToUpdate(csentry);
                }
            }

            AttributeChange anchorChange = AttributeChange.CreateAttributeAdd("objectId", csentry.DN);
            List<AttributeChange> anchorChanges = new List<AttributeChange>() { anchorChange };

            if (!hologram.ObjectClass.IsShadowObject)
            {
                // shadow objects must be provisioned by their parent object, which automatically calls the Commit method
                hologram.CommitCSEntryChange(csentry, isUndeleting);
            }

            referenceRetryRequired = hologram.ReferenceRetryRequired;

            return anchorChanges;
        }

        /// <summary>
        /// Deletes an object from the database
        /// </summary>
        /// <param name="csentryChange">The CSEntryChange containing the object to delete</param>
        /// <param name="dc">The DBDataContext in use on this thread</param>
        private static void PerformCSEntryExportDelete(CSEntryChange csentryChange, MADataContext dc)
        {
            AcmaSchemaObjectClass objectClass = ActiveConfig.DB.GetObjectClass(csentryChange.ObjectType);

            MAObjectHologram maObject = GetObjectFromDnOrAnchor(csentryChange, dc, objectClass);
            csentryChange.DN = maObject.ObjectID.ToString();
            maObject.CommitCSEntryChange(csentryChange, false);
        }

        /// <summary>
        /// Updates an object in the database
        /// </summary>
        /// <param name="csentryChange">The CSEntryChange containing the object to update and its attributes</param>
        /// <param name="dc">The DBDataContext in use on this thread</param>
        /// <param name="referenceRetryRequired">A value indicating whether a reference update failed due to a missing object and needs to be retried after all other CSEntryChanges have been processed</param>
        private static void PerformCSEntryExportUpdate(CSEntryChange csentryChange, MADataContext dc, out bool referenceRetryRequired)
        {
            AcmaSchemaObjectClass objectClass = ActiveConfig.DB.GetObjectClass(csentryChange.ObjectType);
            MAObjectHologram maObject = GetObjectFromDnOrAnchor(csentryChange, dc, objectClass);
            csentryChange.DN = maObject.ObjectID.ToString();
            maObject.CommitCSEntryChange(csentryChange, false);
            referenceRetryRequired = maObject.ReferenceRetryRequired;
        }

        /// <summary>
        /// Replaces an object in the database
        /// </summary>
        /// <param name="csentryChange">The CSEntryChange containing the object to update and its attributes</param>
        /// <param name="dc">The DBDataContext in use on this thread</param>
        /// <param name="referenceRetryRequired">A value indicating whether a reference update failed due to a missing object and needs to be retried after all other CSEntryChanges have been processed</param>
        private static void PerformCSEntryExportReplace(CSEntryChange csentryChange, MADataContext dc, out bool referenceRetryRequired)
        {
            AcmaSchemaObjectClass objectClass = ActiveConfig.DB.GetObjectClass(csentryChange.ObjectType);
            MAObjectHologram maObject = GetObjectFromDnOrAnchor(csentryChange, dc, objectClass);
            csentryChange.DN = maObject.ObjectID.ToString();
            csentryChange.ObjectType = maObject.ObjectClass.Name;
            csentryChange = csentryChange.ConvertCSEntryChangeReplaceToUpdate(maObject);
            maObject.CommitCSEntryChange(csentryChange, false);
            referenceRetryRequired = maObject.ReferenceRetryRequired;
        }

        private static MAObjectHologram ExportShadowObject(CSEntryChange csentry, MADataContext dc)
        {
            if (!csentry.AttributeChanges.Contains("shadowLink"))
            {
                throw new ShadowObjectExportException("The shadowLink attribute was not present");
            }

            if (!csentry.AttributeChanges.Contains("shadowParent"))
            {
                throw new ShadowObjectExportException("The shadowParent attribute was not present");
            }

            ValueChange shadowParentValueChange = csentry.AttributeChanges["shadowParent"].ValueChanges.FirstOrDefault(t => t.ModificationType == ValueModificationType.Add);

            if (shadowParentValueChange == null)
            {
                throw new ShadowObjectExportException("The shadowParent attribute did not contain any value adds");
            }

            if (shadowParentValueChange.Value == null)
            {
                throw new ShadowObjectExportException("The shadowParent attribute was null");
            }

            Guid shadowParentID;

            if (shadowParentValueChange.Value is Guid)
            {
                shadowParentID = (Guid)shadowParentValueChange.Value;
            }
            else
            {
                shadowParentID = new Guid(shadowParentValueChange.Value.ToSmartString());
            }

            MAObjectHologram parentHologram = dc.GetMAObjectOrDefault(shadowParentID);

            if (parentHologram == null)
            {
                throw new ShadowObjectExportException(string.Format("The shadow parent {0} for object {1} was not found", shadowParentID, csentry.DN));
            }

            ValueChange shadowLinkValueChange = csentry.AttributeChanges["shadowLink"].ValueChanges.FirstOrDefault( t=> t.ModificationType == ValueModificationType.Add);

            if (shadowLinkValueChange == null)
            {
                throw new ShadowObjectExportException("The shadowLink attribute did not contain any value adds");
            }

            string shadowLinkID = shadowLinkValueChange.Value as string;

            AcmaSchemaShadowObjectLink link = ActiveConfig.DB.ShadowObjectLinks.FirstOrDefault(t => t.Name == shadowLinkID);

            if (link == null)
            {
                throw new ShadowObjectExportException(string.Format("The shadowLink '{0}' was not found in the database", shadowLinkID));
            }

            AcmaSchemaObjectClass shadowObjectClass = ActiveConfig.DB.ObjectClasses.FirstOrDefault(t => t.Name == csentry.ObjectType);

            if (shadowObjectClass == null)
            {
                throw new NoSuchObjectTypeException(csentry.ObjectType);
            }

            if (shadowObjectClass != link.ShadowObjectClass)
            {
                throw new ShadowObjectExportException(
                    string.Format("The object class exported ({0}) does not match the expected object class ({1}) specified by the link '{2}'",
                    shadowObjectClass.Name, link.ShadowObjectClass, link.Name));
            }

            if (link.ParentObjectClass != parentHologram.ObjectClass)
            {
                throw new ShadowObjectExportException(string.Format(
                    "The specified parent object '{0}' was of the object type '{1}', however the link '{2}' defines '{3}' as the expected parent object type",
                    parentHologram.ObjectID, parentHologram.ObjectClass.Name, link.Name, link.ParentObjectClass.Name));
            }

            AttributeValue provisioningAttribute = parentHologram.GetSVAttributeValue(link.ProvisioningAttribute);

            if (provisioningAttribute.ValueBoolean == true)
            {
                throw new ShadowObjectExportException(string.Format("The shadow parent {0} already has a shadow object referenced by the link '{1}'", shadowParentID, shadowLinkID));
            }

            return parentHologram.ProvisionShadowObject(link, csentry);
            
        }

        /// <summary>
        /// Returns any deleted objects in the database that match the resurrection criteria
        /// </summary>
        /// <param name="csentryChange">The CSEntryChange object representing the object being added to the database</param>
        /// <param name="dc">The DBDataContext in use on this thread</param>
        /// <returns>An MAObject matching the resurrection criteria, or null if no matching object was found</returns>
        private static MAObjectHologram GetResurrectionObject(CSEntryChange csentryChange, MADataContext dc, AcmaSchemaObjectClass objectClass)
        {
            MAObjectHologram existingObject = dc.GetMAObjectOrDefault(csentryChange.DN, objectClass);

            if (existingObject != null)
            {
                return existingObject;
            }

            if (!ActiveConfig.DB.GetObjectClass(csentryChange.ObjectType).AllowResurrection)
            {
                return null;
            }

            if (!ActiveConfig.XmlConfig.ClassConstructors.Contains(csentryChange.ObjectType))
            {
                return null;
            }

            DBQueryGroup parameters = ActiveConfig.XmlConfig.ClassConstructors[csentryChange.ObjectType].ResurrectionParameters;

            if (parameters == null || parameters.DBQueries == null || parameters.DBQueries.Count == 0)
            {
                return null;
            }

            return dc.GetResurrectionObject(parameters, csentryChange);
        }

        private static MAObjectHologram GetObjectFromDnOrAnchor(CSEntryChange csentryChange, MADataContext dc, AcmaSchemaObjectClass objectClass)
        {
            MAObjectHologram maObject = null;
            AcmaSchemaObjectClass schemaObject = null;

            if (!string.IsNullOrWhiteSpace(csentryChange.DN))
            {
                maObject = dc.GetMAObjectOrDefault(new Guid(csentryChange.DN), objectClass);
                if (maObject != null)
                {
                    return maObject;
                }
            }

            if (!string.IsNullOrWhiteSpace(csentryChange.ObjectType))
            {
                schemaObject = ActiveConfig.DB.GetObjectClass(csentryChange.ObjectType);
            }

            DBQueryGroup anchorGroupSearch = new DBQueryGroup(GroupOperator.Any);

            foreach (AnchorAttribute anchor in csentryChange.AnchorAttributes)
            {
                AcmaSchemaAttribute attribute = ActiveConfig.DB.GetAttribute(anchor.Name, schemaObject.Name);
                DBQueryByValue query = new DBQueryByValue(attribute, ValueOperator.Equals, new ValueDeclaration(anchor.Value.ToSmartString()));
                anchorGroupSearch.DBQueries.Add(query);
            }

            if (anchorGroupSearch.DBQueries.Count > 0)
            {
                DBQueryGroup parentGroup;

                if (schemaObject != null)
                {
                    parentGroup = new DBQueryGroup(GroupOperator.All);
                    DBQueryByValue query = new DBQueryByValue(ActiveConfig.DB.GetAttribute("objectClass"), ValueOperator.Equals, new ValueDeclaration(schemaObject.Name));
                    parentGroup.DBQueries.Add(query);
                    parentGroup.DBQueries.Add(anchorGroupSearch);
                }
                else
                {
                    parentGroup = anchorGroupSearch;
                }

                IList<MAObjectHologram> results = dc.GetMAObjectsFromDBQuery(parentGroup).ToList();

                if (results.Count == 1)
                {
                    return results.First();
                }
                else if (results.Count > 1)
                {
                    throw new MultipleMatchException("Multiple objects were returned in the search for an anchor attribute");
                }
            }

            if (string.IsNullOrWhiteSpace(csentryChange.DN))
            {
                throw new InvalidOperationException("The CSEntryChange did not have a DN");
            }

            throw new NoSuchObjectException("The specified object does not exist in the database: " + csentryChange.DN);
        }
    }
}