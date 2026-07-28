// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

namespace Player.Vm.Api.Infrastructure.Options
{
    public class IsoUploadOptions
    {
        public string BasePath { get; set; }
        public long MaxFileSize { get; set; }

        /// <summary>
        /// When true, uploaded ISOs are pushed directly to the vSphere datastore via vCenter's
        /// HTTP file API instead of being written to the local/NFS BasePath. Required for
        /// VMware Cloud on AWS SDDC environments that only support vSAN (no NFS datastores).
        /// Default false leaves existing NFS deployments unchanged.
        /// </summary>
        public bool UploadToDatastore { get; set; } = false;

        /// <summary>
        /// Local directory used to stage the built ISO before it is streamed to the datastore.
        /// When null/empty, the system temp path is used. Only used when UploadToDatastore is true.
        /// </summary>
        public string TempStagingPath { get; set; }

        /// <summary>
        /// Timeout in minutes for the HTTP PUT that uploads an ISO to the datastore. Default 60.
        /// </summary>
        public int UploadTimeoutMinutes { get; set; } = 60;
    }
}
