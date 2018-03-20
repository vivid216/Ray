﻿using Ray.Core;
using Ray.Core.EventSourcing;

namespace Ray.MongoDb
{
    public abstract class MongoRepGrain<K, S, W> : RepGrain<K, S, W>, IMongoGrain
        where S : class, IState<K>, new()
        where W : MessageWrapper
    {
        protected MongoGrainConfig _mongoInfo = null;
        public virtual MongoGrainConfig ESMongoInfo
        {
            get
            {
                return _mongoInfo;
            }
        }
    }
}