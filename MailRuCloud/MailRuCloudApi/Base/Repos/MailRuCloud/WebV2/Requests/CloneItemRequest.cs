﻿using System;
using YaR.MailRuCloud.Api.Base.Requests;
using YaR.MailRuCloud.Api.Base.Requests.Types;

namespace YaR.MailRuCloud.Api.Base.Repos.MailRuCloud.WebV2.Requests
{
    class CloneItemRequest : BaseRequestJson<CommonOperationResult<string>>
    {
        private readonly string _fromUrl;
        private readonly string _toPath;

        public CloneItemRequest(HttpCommonSettings settings, IAuth auth, string fromUrl, string toPath) 
            : base(settings, auth)
        {
            _fromUrl = fromUrl;
            _toPath = toPath;
        }

        protected override string RelationalUri
        {
            get
            {
                var uri = $"{ConstSettings.CloudDomain}/api/v2/clone?conflict=rename&folder={Uri.EscapeDataString(_toPath)}&weblink={Uri.EscapeDataString(_fromUrl)}&token={Auth.AccessToken}";
                return uri;
            }
        }
    }
}