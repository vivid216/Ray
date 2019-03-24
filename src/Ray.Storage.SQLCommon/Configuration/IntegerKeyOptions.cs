﻿using System;

namespace Ray.Storage.SQLCore.Configuration
{
    public class IntegerKeyOptions : StorageOptions
    {
        public IntegerKeyOptions(IServiceProvider serviceProvider, string connectionKey, string uniqueName, long subTableMinutesInterval = 30) : base(serviceProvider)
        {
            ConnectionKey = connectionKey;
            UniqueName = uniqueName;
            SubTableMillionSecondsInterval = subTableMinutesInterval * 24 * 60 * 60 * 1000;
        }
    }
}
