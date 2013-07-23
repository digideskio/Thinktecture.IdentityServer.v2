﻿/*
 * Copyright (c) Dominick Baier, Brock Allen.  All rights reserved.
 * see license.txt
 */

using System.Collections.Generic;

namespace Thinktecture.IdentityServer.Models
{
    public class OpenIdConnectClient
    {
        // general
        public string ClientId { get; set; }
        public string ClientSecret { get; set; }
        public ClientSecretTypes ClientSecretType { get; set; }
        public string Name { get; set; }
        
        // openid connect
        public OpenIdConnectFlows Flow { get; set; }
        public bool AllowRefreshToken { get; set; }
        public int AccessTokenLifetime { get; set; }
        public int RefreshTokenLifetime { get; set; }
        public IEnumerable<string> RedirectUris { get; set; }
        public bool RequireConsent { get; set; }
    }
}