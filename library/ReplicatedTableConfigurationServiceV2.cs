﻿// azure-rtable ver. 0.9
//
// Copyright (c) Microsoft Corporation
//
// All rights reserved.
//
// MIT License
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files
// (the ""Software""), to deal in the Software without restriction, including without limitation the rights to use, copy, modify,
// merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished
// to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.


namespace Microsoft.Azure.Toolkit.Replication
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Linq;

    public class ReplicatedTableConfigurationServiceV2 : IDisposable
    {
        private bool disposed = false;
        private readonly ReplicatedTableConfigurationManager configManager;

        public ReplicatedTableConfigurationServiceV2(List<ConfigurationStoreLocationInfo> blobLocations, bool useHttps, int lockTimeoutInSeconds = 0)
        {
            this.configManager = new ReplicatedTableConfigurationManager(blobLocations, useHttps, lockTimeoutInSeconds, new ReplicatedTableConfigurationParser());
            this.configManager.StartMonitor();
        }

        ~ReplicatedTableConfigurationServiceV2()
        {
            this.Dispose(false);
        }

        public QuorumReadResult RetrieveConfiguration(out ReplicatedTableConfiguration configuration)
        {
            List<string> eTags;

            QuorumReadResult
            result = CloudBlobHelpers.TryReadBlobQuorum(
                                                this.configManager.GetBlobs(),
                                                out configuration,
                                                out eTags,
                                                ReplicatedTableConfiguration.FromJson);

            if (result != QuorumReadResult.Success)
            {
                ReplicatedTableLogger.LogError("Failed to read configuration, result={0}", result);
            }

            return result;
        }

        public List<ReadBlobResult> RetrieveConfiguration(out List<ReplicatedTableConfiguration> configuration)
        {
            List<string> eTagsArray;

            return CloudBlobHelpers.TryReadAllBlobs(
                                                this.configManager.GetBlobs(),
                                                out configuration,
                                                out eTagsArray,
                                                ReplicatedTableConfiguration.FromJson);
        }

        public QuorumWriteResult UpdateConfiguration(ReplicatedTableConfiguration configuration, bool useConditionalUpdate = true)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException("configuration");
            }

            // - Sanitize configuration ...
            foreach (var view in configuration.viewMap)
            {
                var viewName = view.Key;
                var viewConf = view.Value;

                long viewId = viewConf.ViewId;
                View currentView = GetView(viewName);

                if (viewId == 0)
                {
                    if (!currentView.IsEmpty)
                    {
                        viewId = currentView.ViewId + 1;
                    }
                    else
                    {
                        viewId = 1;
                    }
                }

                viewConf.Timestamp = DateTime.UtcNow;
                viewConf.ViewId = viewId;


                // If the read view head index is not 0, this means we are introducing 1 or more replicas at the head.
                // For each such replica, update the view id in which it was added to the write view of the chain
                foreach (var replica in viewConf.GetCurrentReplicaChain())
                {
                    if (replica.IsWriteOnly())
                    {
                        replica.ViewInWhichAddedToChain = viewId;
                        continue;
                    }

                    // stop at the first Readable replica
                    break;
                }
            }

            // - Upload configuration ...
            Func<ReplicatedTableConfiguration, ReplicatedTableConfiguration, bool> comparer = (a, b) => a.Id == b.Id;
            if (!useConditionalUpdate)
            {
                comparer = (a, b) => true;
            }

            QuorumWriteResult
            result = CloudBlobHelpers.TryWriteBlobQuorum(
                                            this.configManager.GetBlobs(),
                                            configuration,
                                            ReplicatedTableConfiguration.FromJson,
                                            comparer,
                                            ReplicatedTableConfiguration.GenerateNewConfigId);

            if (result == QuorumWriteResult.Success)
            {
                this.configManager.Invalidate();
            }
            else
            {
                ReplicatedTableLogger.LogError("Failed to update configuration, result={0}", result);
            }

            return result;
        }

        public bool IsConfiguredTable(string tableName)
        {
            ReplicatedTableConfiguredTable config = this.configManager.FindConfiguredTable(tableName);

            // Neither explicit config, nor default config
            if (config == null)
            {
                return false;
            }

            // Placeholder config i.e. a config with No View
            if (string.IsNullOrEmpty(config.ViewName))
            {
                return false;
            }

            return true;
        }

        public View GetView(string viewName)
        {
            return this.configManager.GetView(viewName);
        }

        /*
         * Helper APIs
         */
        public void TurnReplicaOn(string storageAccountName)
        {
        }

        public void TurnReplicaOff(string storageAccountName)
        {
            if (string.IsNullOrEmpty(storageAccountName))
            {
                throw new ArgumentNullException("storageAccountName");
            }

            ReplicatedTableConfiguration configuration = null;

            // - Retrieve configuration ...
            QuorumReadResult readResult = RetrieveConfiguration(out configuration);
            if (readResult != QuorumReadResult.Success)
            {
                ReplicatedTableLogger.LogError("TurnReplicaOff={0}: failed to read configuration, result={1}", storageAccountName, readResult);

                var msg = string.Format("TurnReplicaOff={0}: failed to read configuration, result={1}", storageAccountName, readResult);
                throw new Exception(msg);
            }

            // - Parse/Update all views ...
            foreach (var viewConf in configuration.viewMap.Values)
            {
                var foundReplicas = viewConf.GetCurrentReplicaChain()
                                            .FindAll(r => r.StorageAccountName == storageAccountName);

                foreach (var replica in foundReplicas)
                {
                    replica.Status = ReplicaStatus.None;
                }
            }

            // - Write back configuration ...
            QuorumWriteResult writeResult = UpdateConfiguration(configuration);
            if (writeResult != QuorumWriteResult.Success)
            {
                ReplicatedTableLogger.LogError("TurnReplicaOff={0}: failed to update configuration, result={1}", storageAccountName, writeResult);

                var msg = string.Format("TurnReplicaOff={0}: failed to update configuration, result={1}", storageAccountName, writeResult);
                throw new Exception(msg);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (this.disposed)
            {
                return;
            }

            if (disposing)
            {
                this.configManager.StopMonitor();
            }

            this.disposed = true;
        }
        // ...
    }
}