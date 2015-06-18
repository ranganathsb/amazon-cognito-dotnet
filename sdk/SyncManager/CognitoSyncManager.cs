//
// Copyright 2014-2015 Amazon.com, 
// Inc. or its affiliates. All Rights Reserved.
// 
// Licensed under the Amazon Software License (the "License"). 
// You may not use this file except in compliance with the 
// License. A copy of the License is located at
// 
//     http://aws.amazon.com/asl/
// 
// or in the "license" file accompanying this file. This file is 
// distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, express or implied. See the License 
// for the specific language governing permissions and 
// limitations under the License.
//

using System;
using System.Collections.Generic;

using Amazon.Runtime;
using Amazon.CognitoSync.SyncManager.Internal;
using Amazon.CognitoIdentity;
using Amazon.Runtime.Internal.Util;
using Amazon.Util.Internal;
using System.Threading.Tasks;
using System.Threading;

namespace Amazon.CognitoSync.SyncManager
{
    /// <summary>
    /// The Cognito Sync Manager for Dot Net allows your application to store data 
    /// in the cloud for your users and synchronize across other devices. The library 
    /// uses the sqlite for local storage API and defaults to inmemory where sqlite 
    /// is not available to create a local cache for the data, similar to our SDK. 
    /// This allows your application to access stored data even when there is no connectivity.
    /// <code>
    /// CognitoAWSCredentials credentials = new CognitoAWSCredentials(&quot;identityPoolId&quot;,&quot;RegionEndpoint&quot;)
    /// //using default region from your app.config or awsconfig.xml
    /// CognitoSyncManager cognitoSyncManager = new CognitoSyncManager(credentials);
    /// // creating a dataset
    /// Dataset playerInfo = cognitoSyncManager.OpenOrCreateDataset(&quot;playerInfo&quot;);
    /// // add some values into your dataset
    /// playerInfo.Put(&quot;high_score&quot;, &quot;90&quot;);
    /// playerInfo.Put(&quot;name&quot;, &quot;John&quot;);
    /// // push changes to remote if needed
    /// playerInfo.synchronize();
    /// </code>
    /// </summary>
    public partial class CognitoSyncManager : IDisposable
    {
        private Logger _logger;
        private bool _disposed;

        private readonly ILocalStorage Local;

        private readonly CognitoSyncStorage Remote;

        private readonly CognitoAWSCredentials CognitoCredentials;

        #region Constructor

        /// <summary>
        /// Creates an instance of CognitoSyncManager using Cognito Credentials, the region is picked up from the config if it 
        /// <code>
        /// CognitoSyncManager cognitoSyncManager = new CognitoSyncManager(credentials)
        /// </code>
        /// </summary>
        /// <param name="cognitoCredentials"><see cref="Amazon.CognitoIdentity.CognitoAWSCredentials"/></param>
        public CognitoSyncManager(CognitoAWSCredentials cognitoCredentials) : this(cognitoCredentials, new AmazonCognitoSyncConfig()) { }

        /// <summary>
        /// Creates an instance of CognitoSyncManager using cognito credentials and a specific region
        /// <code>
        /// CognitoSyncManager cognitoSyncManager = new CognitoSyncManager(credentials, RegionEndpoint.USEAST1)
        /// </code>
        /// </summary>
        /// <param name="cognitoCredentials"><see cref="Amazon.CognitoIdentity.CognitoAWSCredentials"/></param>
        /// <param name="endpoint"><see cref="Amazon.RegionEndpoint"/></param>
        public CognitoSyncManager(CognitoAWSCredentials cognitoCredentials, RegionEndpoint endpoint)
            : this(cognitoCredentials, new AmazonCognitoSyncConfig
            {
                RegionEndpoint = endpoint
            })
        { }

        /// <summary>
        /// Creates an instance of CognitoSyncManager using cognito credentials and a configuration object
        /// <code>
        /// CognitoSyncManager cognitoSyncManager = new CognitoSyncManager(credentials,new AmazonCognitoSyncConfig { RegionEndpoint =  RegionEndpoint.USEAST1})
        /// </code>
        /// </summary>
        /// <param name="cognitoCredentials"><see cref="Amazon.CognitoIdentity.CognitoAWSCredentials"/></param>
        /// <param name="config"><see cref="Amazon.CognitoSync.AmazonCognitoSyncConfig"/></param>
        public CognitoSyncManager(CognitoAWSCredentials cognitoCredentials, AmazonCognitoSyncConfig config)
        {
            if (cognitoCredentials == null)
            {
                throw new ArgumentNullException("cognitoCredentials");
            }

            if (string.IsNullOrEmpty(cognitoCredentials.IdentityPoolId))
            {
                throw new ArgumentNullException("cognitoCredentials.IdentityPoolId");
            }
            this.CognitoCredentials = cognitoCredentials;

            Local = new SQLiteLocalStorage();

            Remote = new CognitoSyncStorage(cognitoCredentials, config);

            cognitoCredentials.IdentityChangedEvent += this.IdentityChanged;

            _logger = Logger.GetLogger(this.GetType());
        }

        #endregion

        #region Dispose Methods

        /// <summary>
        /// Releases the resources consumed by this object
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the resources consumed by this object if disposing is true. 
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                CognitoCredentials.IdentityChangedEvent -= this.IdentityChanged;
                _disposed = true;
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Opens or creates a dataset. If the dataset doesn't exist, an empty one
        /// with the given name will be created. Otherwise, the dataset is loaded from
        /// local storage. If a dataset is marked as deleted but hasn't been deleted
        /// on remote via <see cref="Amazon.CognitoSync.SyncManager.CognitoSyncManager.RefreshDatasetMetadataAsync"/>, 
        /// it will throw <see cref="System.InvalidOperationException"/>.
        /// <code>
        /// Dataset dataset = cognitoSyncManager.OpenOrCreateDataset("myDatasetName");
        /// </code>
        /// </summary>
        /// <returns>Dataset loaded from local storage</returns>
        /// <param name="datasetName">DatasetName dataset name, must be [a-zA-Z0=9_.:-]+</param>
        public Dataset OpenOrCreateDataset(string datasetName)
        {
            DatasetUtils.ValidateDatasetName(datasetName);
            Local.CreateDataset(GetIdentityId(), datasetName);
            return new Dataset(datasetName, CognitoCredentials, Local, Remote);
        }


        /// <summary>
        /// Retrieves a list of datasets from local storage. It may not reflects
        /// latest dataset on the remote storage until <see cref="Amazon.CognitoSync.SyncManager.CognitoSyncManager.RefreshDatasetMetadataAsync"/> is
        /// called.
        /// </summary>
        /// <returns>List of datasets</returns>
        public List<DatasetMetadata> ListDatasets()
        {
            return Local.GetDatasetMetadata(GetIdentityId());
        }

        /// <summary>
        /// Wipes all user data cached locally, including identity id, session
        /// credentials, dataset metadata, and all records. Any data that hasn't been
        /// synced will be lost. This method is usually used when customer logs out.
        /// </summary>
        public void WipeData()
        {
            Local.WipeData();
            _logger.InfoFormat("All data has been wiped");
        }

        #endregion

        #region Protected Methods
        /// <summary>
        /// This is triggered when an Identity Change event occurs. 
        /// The dataset are then remapped to the new identity id.
        /// This may happend for example when a user is working with 
        /// unauthenticated id and later decides to authenticate 
        /// himself with a public login provider
        /// </summary>
        /// <param name="sender">The object which triggered this methos</param>
        /// <param name="e">Event Arguments</param>
        protected void IdentityChanged(object sender, EventArgs e)
        {
            var identityChangedEvent = e as Amazon.CognitoIdentity.CognitoAWSCredentials.IdentityChangedArgs;
            String oldIdentity = identityChangedEvent.OldIdentityId == null ? DatasetUtils.UNKNOWN_IDENTITY_ID : identityChangedEvent.OldIdentityId;
            String newIdentity = identityChangedEvent.NewIdentityId == null ? DatasetUtils.UNKNOWN_IDENTITY_ID : identityChangedEvent.NewIdentityId;
            _logger.InfoFormat("identity change detected: {0}, {1}", oldIdentity, newIdentity);
            if (oldIdentity != newIdentity) Local.ChangeIdentityId(oldIdentity, newIdentity);
        }

        /// <summary>
        /// Returns the IdentityId, if the application is not online then an 
        /// Unknown Identity Will be returned
        /// </summary>
        /// <returns>Identity ID</returns>
        protected string GetIdentityId()
        {
            return DatasetUtils.GetIdentityId(CognitoCredentials);
        }

        /// <summary>
        /// Refreshes dataset metadata. Dataset metadata is pulled from remote
        /// storage and stored in local storage. Their record data isn't pulled down
        /// until you sync each dataset.
        /// </summary>
        /// <param name="cancellationToken">
        ///     A cancellation token that can be used by other objects or threads to receive notice of cancellation.
        /// </param>
        /// <exception cref="Amazon.CognitoSync.SyncManager.DataStorageException">Thrown when fail to fresh dataset metadata</exception>
        public async Task<List<DatasetMetadata>> RefreshDatasetMetadataAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            List<DatasetMetadata> response = await Remote.GetDatasetMetadataAsync(cancellationToken);
            Local.UpdateDatasetMetadata(GetIdentityId(), response);
            return response;
        }

        #endregion

    }
}
