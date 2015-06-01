﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Lithnet.Common.Presentation;
using Lithnet.Common.ObjectModel;
using Microsoft.Win32;
using System.ComponentModel;
using System.Linq;
using Lithnet.Acma;
using Lithnet.Acma.Presentation;
using System.Collections.ObjectModel;
using System.Collections;
using Lithnet.Fim.Transforms;
using Lithnet.Fim.Transforms.Presentation;
using System.Windows.Media.Imaging;
using System.Runtime.Serialization;
using Lithnet.Acma.TestEngine;
using Lithnet.Logging;
using Lithnet.Acma.DataModel;
using Novacode;
using System.Text;

namespace Lithnet.Acma.Presentation
{
    public class XmlConfigFileViewModel : ViewModelBase<XmlConfigFile>
    {
        private UnitTestFile unitTestFile;

        private UnitTestFileViewModel unitTestFileVM;

        private ClassConstructorsViewModel constructors;

        private TransformCollectionViewModel transforms;

        private AcmaOperationEventsViewModel operationEvents;

        public XmlConfigFileViewModel()
        {
            this.Initialize();
            this.New();
            this.ReloadRootNodes();
        }

        public XmlConfigFileViewModel(XmlConfigFile model)
            : base(model)
        {
            this.Initialize();
            this.ReloadRootNodes();
        }

        private void ReloadRootNodes()
        {
            if (this.Model.OperationEvents == null)
            {
                this.Model.OperationEvents = new AcmaEvents();
            }

            ActiveConfig.DB.CanCache = true;

            this.OperationEvents = new AcmaOperationEventsViewModel(this.Model.OperationEvents);
            this.Transforms = new TransformCollectionViewModel(this.Model.Transforms);
            this.Constructors = new ClassConstructorsViewModel(this.Model.ClassConstructors);
            this.UnitTestFile = new UnitTestFileViewModel(this.unitTestFile);

            ActiveConfig.DB.CanCache = false;
        }

        private void Initialize()
        {
            this.IgnorePropertyHasChanged.Add("ConfigItems");
            this.IgnorePropertyHasChanged.Add("Transforms");
            this.IgnorePropertyHasChanged.Add("Constructors");
            this.IgnorePropertyHasChanged.Add("UnitTests");
            this.IgnorePropertyHasChanged.Add("DisplayName");
        }

        public string FileName
        {
            get
            {
                return this.Model.FileName;
            }
        }

        public string DisplayName
        {
            get
            {
                if (string.IsNullOrWhiteSpace(this.FileName))
                {
                    return string.Format("Configuration (new file)");
                }
                else
                {
                    return string.Format("Configuration ({0})", Path.GetFileName(this.FileName));
                }
            }
        }

        public string ConfigVersion
        {
            get
            {
                return this.Model.ConfigVersion;
            }
        }

        public string Description
        {
            get
            {
                return this.Model.Description;
            }
            set
            {
                this.Model.Description = value;
            }
        }

        public TransformCollectionViewModel Transforms
        {
            get
            {
                return this.transforms;
            }
            set
            {
                if (this.transforms != null)
                {
                    this.UnregisterChildViewModel(this.transforms);
                }

                this.transforms = value;
                this.RegisterChildViewModel(this.transforms);
            }
        }

        public ClassConstructorsViewModel Constructors
        {
            get
            {
                return this.constructors;
            }
            set
            {
                if (this.constructors != null)
                {
                    this.UnregisterChildViewModel(this.constructors);
                }

                this.constructors = value;
                this.RegisterChildViewModel(this.constructors);
            }
        }

        public AcmaOperationEventsViewModel OperationEvents
        {
            get
            {
                return this.operationEvents;
            }
            set
            {
                if (this.operationEvents != null)
                {
                    this.UnregisterChildViewModel(this.operationEvents);
                }

                this.operationEvents = value;
                this.RegisterChildViewModel(this.operationEvents);
            }
        }

        public UnitTestFileViewModel UnitTestFile
        {
            get
            {
                return this.unitTestFileVM;
            }
            set
            {
                if (this.unitTestFileVM != null)
                {
                    this.UnregisterChildViewModel(this.unitTestFileVM);
                }

                this.unitTestFileVM = value;
                this.RegisterChildViewModel(this.unitTestFileVM);
            }
        }

        public IEnumerable<object> ConfigItems
        {
            get
            {
                yield return this.Transforms;
                yield return this.OperationEvents;
                yield return this.Constructors;
                yield return this.UnitTestFile;
            }
        }

        //private void SetupUnitTestObject()
        //{
        //    this.unitTestFile = TestEngine.UnitTestFile.LoadXml(this.FileName);
        //    //if (this.HasUnitTestFile(this.FileName))
        //    //{
        //    //    this.unitTestFile = TestEngine.UnitTestFile.LoadXml(this.GetUnitTestFileName(this.FileName));
        //    //}
        //}

        public void Open(string filename)
        {
            XmlConfigFile existingModel = this.Model;
            UnitTestFileViewModel existingUTModel = this.UnitTestFile;

            try
            {
                ActiveConfig.DB.CanCache = true;
                AcmaSchemaAttribute.MissingAttributeEvent += AcmaSchemaAttribute_MissingAttributeEvent;
                UINotifyPropertyChanges.BeginIgnoreAllChanges();
                this.Model = ActiveConfig.LoadXml(filename);
                this.unitTestFile = TestEngine.UnitTestFile.LoadXml(this.FileName);
                //this.SetupUnitTestObject();
                this.ReloadRootNodes();
                this.ResetChangeState();
            }
            catch
            {
                this.Model = existingModel;
                this.UnitTestFile = existingUTModel;
                throw;
            }
            finally
            {
                UINotifyPropertyChanges.EndIgnoreAllChanges();
                this.RaisePropertyChanged("DisplayName");
                this.RaisePropertyChanged("ConfigItems");
                AcmaSchemaAttribute.ClearLostFoundCache();
                AcmaSchemaAttribute.MissingAttributeEvent -= AcmaSchemaAttribute_MissingAttributeEvent;
                ActiveConfig.DB.CanCache = false;
            }
        }

        private void AcmaSchemaAttribute_MissingAttributeEvent(MissingAttributeEventArgs args)
        {
            MissingAttributeWindow window = new MissingAttributeWindow();
            MissingAttributeViewModel vm = new MissingAttributeViewModel(window);
            vm.MissingAttributeName = args.MissingAttributeName;
            window.DataContext = vm;

            bool? result = window.ShowDialog();

            if (result.HasValue && result.Value)
            {
                args.ReplacementAttribute = vm.Attribute;
            }
        }

        public void New()
        {
            ActiveConfig.DB.CanCache = true;
            UINotifyPropertyChanges.BeginIgnoreAllChanges();
            UniqueIDCache.ClearIdCache();
            this.Model = new XmlConfigFile();
            this.unitTestFile = new TestEngine.UnitTestFile();
            ActiveConfig.XmlConfig = this.Model;
            UINotifyPropertyChanges.EndIgnoreAllChanges();
            this.ReloadRootNodes();

            this.RaisePropertyChanged("DisplayName");
            this.RaisePropertyChanged("ConfigItems");
            this.ResetChangeState();
            ActiveConfig.DB.CanCache = false;
        }

        public void Save(string filename)
        {
            XmlConfigWrapper wrapper = new XmlConfigWrapper();
            wrapper.AcmaConfig = ActiveConfig.XmlConfig;
            wrapper.UnitTests = this.unitTestFile;

            Dictionary<string, string> namespaces = new Dictionary<string, string>();
            namespaces.Add("t", "http://lithnet.local/Lithnet.IdM.Transforms/v1/");
            namespaces.Add("a", "http://schemas.microsoft.com/2003/10/Serialization/Arrays");

            Serializer.Save<XmlConfigWrapper>(filename, wrapper, namespaces);

            ActiveConfig.XmlConfig.FileName = filename;
            //ActiveConfig.Save(filename, this.Model);
            //this.SaveUnitTestFile();
            this.RaisePropertyChanged("DisplayName");
            
            this.ResetChangeState();
        }

        //private void SaveUnitTestFile()
        //{
        //    if (this.unitTestFile == null || this.unitTestFile.UnitTestObjects.Count == 0)
        //    {
        //        return;
        //    }

        //    string filename = this.GetUnitTestFileName(this.FileName);
        //    this.unitTestFile.Save(filename);
        //}

        //private bool HasUnitTestFile(string configFileName)
        //{
        //    return File.Exists(this.GetUnitTestFileName(configFileName));
        //}

        //private string GetUnitTestFileName(string configFileName)
        //{
        //    string file = Path.GetFileNameWithoutExtension(configFileName) + ".acmax.ut";
        //    string directory = Path.GetDirectoryName(configFileName);

        //    return Path.Combine(directory, file);
        //}
    }
}
