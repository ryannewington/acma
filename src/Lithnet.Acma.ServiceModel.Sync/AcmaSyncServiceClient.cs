﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.ServiceModel;
using Microsoft.MetadirectoryServices;
using System.Runtime.Serialization;
using System.ServiceModel.Description;
using Lithnet.MetadirectoryServices;

namespace Lithnet.Acma.ServiceModel
{
    public class AcmaSyncServiceClient : ClientBase<IAcmaSyncService>, IAcmaSyncService
    {
        public AcmaSyncServiceClient()
            : base(SyncServiceConfig.NetNamedPipeBinding, SyncServiceConfig.NetNamedPipeEndpointAddress)
        {
            foreach (OperationDescription op in this.ChannelFactory.Endpoint.Contract.Operations)
            {
                DataContractSerializerOperationBehavior dataContractBehavior =
                    op.Behaviors.Find<DataContractSerializerOperationBehavior>()
                    as DataContractSerializerOperationBehavior;
                if (dataContractBehavior != null)
                {
                    dataContractBehavior.DataContractSurrogate = new MmsSerializationSurrogate();
                }
                else
                {
                    dataContractBehavior = new DataContractSerializerOperationBehavior(op);
                    dataContractBehavior.DataContractSurrogate = new MmsSerializationSurrogate();
                    op.Behaviors.Add(dataContractBehavior);
                }
            }
        }
       
        public string GetCurrentWatermark()
        {
            return this.Channel.GetCurrentWatermark();
        }

        public void ExportStart()
        {
            this.Channel.ExportStart();
        }

        public void ExportEnd()
        {
            this.Channel.ExportEnd();
        }

        public ExportResponse ExportPage(ExportRequest request)
        {
            return this.Channel.ExportPage(request);
        }

        public ImportResponse ImportStart(ImportStartRequest request)
        {
            return this.Channel.ImportStart(request);
        }

        public ImportResponse ImportPage(PageRequest request)
        {
            return this.Channel.ImportPage(request);
        }

        public Schema GetMmsSchema()
        {
            return this.Channel.GetMmsSchema();
        }

        public void ImportEnd(ImportReleaseRequest request)
        {
            this.Channel.ImportEnd(request);
        }
    }
}
