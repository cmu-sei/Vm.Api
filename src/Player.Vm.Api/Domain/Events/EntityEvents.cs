// Copyright 2021 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using MediatR;

namespace Player.Vm.Api.Domain.Events
{
    public class EntityCreated<TEntity> : INotification
    {
        public TEntity Entity { get; set; }

        public EntityCreated(TEntity entity)
        {
            Entity = entity;
        }
    }

    public class EntityUpdated<TEntity> : INotification
    {
        public TEntity Entity { get; set; }
        public string[] ModifiedProperties { get; set; }

        public EntityUpdated(TEntity entity, string[] modifiedProperties)
        {
            Entity = entity;
            ModifiedProperties = modifiedProperties;
        }
    }

    public class EntityDeleted<TEntity> : INotification
    {
        public TEntity Entity { get; set; }

        public EntityDeleted(TEntity entity)
        {
            Entity = entity;
        }
    }
}
