﻿using YaR.Clouds.Base.Repos;

namespace YaR.Clouds.Base.Requests
{
    internal abstract class BaseRequestString : BaseRequestString<string>
    {
        protected BaseRequestString(HttpCommonSettings settings, IAuth auth) 
            : base(settings, auth)
        {
        }

        protected override RequestResponse<string> DeserializeMessage(string data)
        {
            var msg = new RequestResponse<string>
            {
                Ok = true,
                Result = data
            };
            return msg;
        }
    }
}