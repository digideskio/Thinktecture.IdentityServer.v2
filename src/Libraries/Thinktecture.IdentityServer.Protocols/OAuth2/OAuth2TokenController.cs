﻿/*
 * Copyright (c) Dominick Baier, Brock Allen.  All rights reserved.
 * see license.txt
 */

using System.ComponentModel.Composition;
using System.IdentityModel.Protocols.WSTrust;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Web.Http;
using Thinktecture.IdentityModel.Authorization;
using Thinktecture.IdentityServer.Models;
using Thinktecture.IdentityServer.Repositories;

namespace Thinktecture.IdentityServer.Protocols.OAuth2
{
    public class OAuth2TokenController : ApiController
    {
        [Import]
        public IUserRepository UserRepository { get; set; }

        [Import]
        public IConfigurationRepository ConfigurationRepository { get; set; }

        [Import]
        public IClientsRepository ClientsRepository { get; set; }

        [Import]
        public ICodeTokenRepository RefreshTokenRepository { get; set; }

        public OAuth2TokenController()
        {
            Container.Current.SatisfyImportsOnce(this);
        }

        public OAuth2TokenController(IUserRepository userRepository, IConfigurationRepository configurationRepository, IClientsRepository clientsRepository, ICodeTokenRepository refreshTokenRepository)
        {
            UserRepository = userRepository;
            ConfigurationRepository = configurationRepository;
            ClientsRepository = clientsRepository;
            RefreshTokenRepository = refreshTokenRepository;
        }

        public HttpResponseMessage Post([FromBody] TokenRequest tokenRequest)
        {
            Tracing.Information("OAuth2 endpoint called.");

            Client client = null;
            var error = ValidateRequest(tokenRequest, out client);
            if (error != null) return error;

            Tracing.Information("Client: " + client.Name);

            // read token type from configuration (typically JWT)
            var tokenType = ConfigurationRepository.Global.DefaultHttpTokenType;

            // switch over the grant type
            if (tokenRequest.Grant_Type.Equals(OAuth2Constants.GrantTypes.Password))
            {
                return ProcessResourceOwnerCredentialRequest(tokenRequest, tokenType, client);
            }
            else if (tokenRequest.Grant_Type.Equals(OAuth2Constants.GrantTypes.AuthorizationCode))
            {
                return ProcessAuthorizationCodeRequest(client, tokenRequest.Code, tokenType);
            }
            else if (string.Equals(tokenRequest.Grant_Type, OAuth2Constants.GrantTypes.RefreshToken, System.StringComparison.Ordinal))
            {
                return ProcessRefreshTokenRequest(client, tokenRequest.Refresh_Token, tokenType);
            }

            Tracing.Error("invalid grant type: " + tokenRequest.Grant_Type);
            return OAuthErrorResponseMessage(OAuth2Constants.Errors.UnsupportedGrantType);
        }

        private HttpResponseMessage ProcessResourceOwnerCredentialRequest(TokenRequest request, string tokenType, Client client)
        {
            Tracing.Information("Starting resource owner password credential flow for client: " + client.Name);
            var appliesTo = new EndpointReference(request.Scope);

            if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
            {
                Tracing.Error("Invalid resource owner credentials for: " + appliesTo.Uri.AbsoluteUri);
                return OAuthErrorResponseMessage(OAuth2Constants.Errors.InvalidGrant);
            }

            if (UserRepository.ValidateUser(request.UserName, request.Password))
            {
                return CreateTokenResponse(request.UserName, client, appliesTo, tokenType, includeRefreshToken: client.AllowRefreshToken);
            }
            else
            {
                Tracing.Error("Resource owner credential validation failed: " + request.UserName);
                return OAuthErrorResponseMessage(OAuth2Constants.Errors.InvalidGrant);
            }
        }

        private HttpResponseMessage ProcessAuthorizationCodeRequest(Client client, string code, string tokenType)
        {
            return ProcessRefreshTokenRequest(client, code, tokenType);
        }

        private HttpResponseMessage ProcessRefreshTokenRequest(Client client, string refreshToken, string tokenType)
        {
            Tracing.Information("Processing refresh token request for client: " + client.Name);

            // 1. get refresh token from DB - if not exists: error
            CodeToken token;
            if (RefreshTokenRepository.TryGetCode(refreshToken, out token))
            {
                // 2. make sure the client is the same - if not: error
                if (token.ClientId == client.ID)
                {
                    // 3. call STS 
                    RefreshTokenRepository.DeleteCode(token.Code);
                    return CreateTokenResponse(token.UserName, client, new EndpointReference(token.Scope), tokenType, includeRefreshToken: client.AllowRefreshToken);
                }

                Tracing.Error("Invalid client for refresh token. " + client.Name + " / " + refreshToken);
                return OAuthErrorResponseMessage(OAuth2Constants.Errors.InvalidGrant);
            }

            Tracing.Error("Refresh token not found. " + client.Name + " / " + refreshToken);
            return OAuthErrorResponseMessage(OAuth2Constants.Errors.InvalidGrant);
        }

        private HttpResponseMessage CreateTokenResponse(string userName, Client client, EndpointReference scope, string tokenType, bool includeRefreshToken)
        {
            var auth = new AuthenticationHelper();

            var principal = auth.CreatePrincipal(userName, "OAuth2",
                    new Claim[]
                        {
                            new Claim(Constants.Claims.Client, client.Name),
                            new Claim(Constants.Claims.Scope, scope.Uri.AbsoluteUri)
                        });

            if (!ClaimsAuthorization.CheckAccess(principal, Constants.Actions.Issue, Constants.Resources.OAuth2))
            {
                Tracing.Error("OAuth2 endpoint authorization failed for user: " + userName);
                return OAuthErrorResponseMessage(OAuth2Constants.Errors.InvalidGrant);
            }

            var sts = new STS();
            TokenResponse tokenResponse;
            if (sts.TryIssueToken(scope, principal, tokenType, out tokenResponse))
            {
                if (includeRefreshToken)
                {
                    tokenResponse.RefreshToken = RefreshTokenRepository.AddCode(CodeTokenType.RefreshTokenIdentifier, client.ID, userName, scope.Uri.AbsoluteUri);
                }

                var resp = Request.CreateResponse<TokenResponse>(HttpStatusCode.OK, tokenResponse);
                return resp;
            }
            else
            {
                return OAuthErrorResponseMessage(OAuth2Constants.Errors.InvalidRequest);
            }
        }

        private HttpResponseMessage OAuthErrorResponseMessage(string error)
        {
            return Request.CreateErrorResponse(HttpStatusCode.BadRequest,
                string.Format("{{ \"{0}\": \"{1}\" }}", OAuth2Constants.Errors.Error, error));
        }

        private HttpResponseMessage ValidateRequest(TokenRequest request, out Client client)
        {
            client = null;

            // grant type is required
            if (string.IsNullOrWhiteSpace(request.Grant_Type))
            {
                return OAuthErrorResponseMessage(OAuth2Constants.Errors.UnsupportedGrantType);
            }

            // check supported grant types
            if (!request.Grant_Type.Equals(OAuth2Constants.GrantTypes.AuthorizationCode) &&
                !request.Grant_Type.Equals(OAuth2Constants.GrantTypes.Password) &&
                !request.Grant_Type.Equals(OAuth2Constants.GrantTypes.RefreshToken))
            {
                return OAuthErrorResponseMessage(OAuth2Constants.Errors.UnsupportedGrantType);
            }

            // resource owner password flow requires a well-formed scope
            EndpointReference appliesTo = null;
            if (request.Grant_Type.Equals(OAuth2Constants.GrantTypes.Password))
            {
                try
                {
                    appliesTo = new EndpointReference(request.Scope);
                    Tracing.Information("OAuth2 endpoint called for scope: " + request.Scope);
                }
                catch
                {
                    Tracing.Error("Malformed scope: " + request.Scope);
                    return OAuthErrorResponseMessage(OAuth2Constants.Errors.InvalidScope);
                }
            }

            if (!ValidateClient(out client))
            {
                Tracing.Error("Invalid client: " + ClaimsPrincipal.Current.Identity.Name);
                return OAuthErrorResponseMessage(OAuth2Constants.Errors.InvalidClient);
            }

            // validate grant types against global and client configuration
            if (request.Grant_Type.Equals(OAuth2Constants.GrantTypes.AuthorizationCode))
            {
                if (!ConfigurationRepository.OAuth2.EnableCodeFlow ||
                    !client.AllowCodeFlow)
                {
                    return OAuthErrorResponseMessage(OAuth2Constants.Errors.UnsupportedGrantType);
                }
            }

            if (request.Grant_Type.Equals(OAuth2Constants.GrantTypes.Password))
            {
                if (!ConfigurationRepository.OAuth2.EnableResourceOwnerFlow ||
                    !client.AllowResourceOwnerFlow)
                {
                    return OAuthErrorResponseMessage(OAuth2Constants.Errors.UnsupportedGrantType);
                }
            }

            if (request.Grant_Type.Equals(OAuth2Constants.GrantTypes.RefreshToken))
            {
                if (!client.AllowRefreshToken)
                {
                    return OAuthErrorResponseMessage(OAuth2Constants.Errors.UnsupportedGrantType);
                }
            }

            return null;
        }

        private bool ValidateClient(out Client client)
        {
            client = null;

            if (!ClaimsPrincipal.Current.Identity.IsAuthenticated)
            {
                Tracing.Error("Anonymous client.");
                return false;
            }

            var passwordClaim = ClaimsPrincipal.Current.FindFirst("password");
            if (passwordClaim == null)
            {
                Tracing.Error("No client secret provided.");
                return false;
            }

            return ClientsRepository.ValidateAndGetClient(
                ClaimsPrincipal.Current.Identity.Name,
                passwordClaim.Value,
                out client);
        }
    }
}