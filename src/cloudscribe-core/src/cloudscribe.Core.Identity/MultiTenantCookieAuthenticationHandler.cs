﻿// Copyright (c) Source Tree Solutions, LLC. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.
// Author:					Joe Audette
// Created:					2015-07-31
// Last Modified:			2015-08-01
// 



using Microsoft.AspNet.Authentication;
using Microsoft.AspNet.Authentication.Cookies;
using Microsoft.AspNet.DataProtection;
using Microsoft.AspNet.Authentication.DataHandler;
using Microsoft.AspNet.Http;
using Microsoft.AspNet.Http.Authentication;
using Microsoft.AspNet.Http.Features.Authentication;
using Microsoft.Framework.Logging;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;


namespace cloudscribe.Core.Identity
{
    // this implementation is based on the file:
    //https://github.com/aspnet/Security/blob/dev/src/Microsoft.AspNet.Authentication.Cookies/CookieAuthenticationHandler.cs
    // as such we need to keep an eye on that file for any changes that we may need to sync here
    // any code we change from the default code we should comment out the default code so that we can still compare it to the current version of the file

    //TODO: modify this to support multi tenants
    internal class MultiTenantCookieAuthenticationHandler : AuthenticationHandler<CookieAuthenticationOptions>
    {
        private const string HeaderNameCacheControl = "Cache-Control";
        private const string HeaderNamePragma = "Pragma";
        private const string HeaderNameExpires = "Expires";
        private const string HeaderValueNoCache = "no-cache";
        private const string HeaderValueMinusOne = "-1";
        private const string SessionIdClaim = "Microsoft.AspNet.Authentication.Cookies-SessionId";

        private bool _shouldRenew;
        private DateTimeOffset? _renewIssuedUtc;
        private DateTimeOffset? _renewExpiresUtc;
        private string _sessionKey;
        private Task<AuthenticationTicket> _cookieTicketTask;

        public MultiTenantCookieAuthenticationHandler(
            IDataProtectionProvider dataProtectionProvider,
            MultiTenantCookieOptionsResolver tenantResolver) :base()
        {
            this.dataProtectionProvider = dataProtectionProvider;
            this.tenantResolver = tenantResolver;
        }

        private Task<AuthenticationTicket> EnsureCookieTicket()
        {
            // We only need to read the ticket once
            if (_cookieTicketTask == null)
            {
                _cookieTicketTask = ReadCookieTicket();
            }
            return _cookieTicketTask;
        }

        private MultiTenantCookieOptionsResolver tenantResolver;
        private IDataProtectionProvider dataProtectionProvider;

        private ISecureDataFormat<AuthenticationTicket> GetTicketDataFormat()
        {
            var dataProtector = dataProtectionProvider.CreateProtector(
                typeof(CookieAuthenticationMiddleware).FullName, tenantResolver.ResolveAuthScheme(Options.AuthenticationScheme), "v2");

            return new TicketDataFormat(dataProtector);
        }

        private async Task<AuthenticationTicket> ReadCookieTicket()
        {
            //var cookie = Options.CookieManager.GetRequestCookie(Context, Options.CookieName);
            tenantResolver.Reset(); 
            var cookie = Options.CookieManager.GetRequestCookie(Context, tenantResolver.ResolveCookieName(Options.CookieName));
            if (string.IsNullOrEmpty(cookie))
            {
                return null;
            }

            //var ticket = Options.TicketDataFormat.Unprotect(cookie);
            var ticketFormat = GetTicketDataFormat();
            var ticket = ticketFormat.Unprotect(cookie);


            if (ticket == null)
            {
                Logger.LogWarning(@"Unprotect ticket failed");
                return null;
            }

            if (Options.SessionStore != null)
            {
                var claim = ticket.Principal.Claims.FirstOrDefault(c => c.Type.Equals(SessionIdClaim));
                if (claim == null)
                {
                    Logger.LogWarning(@"SessionId missing");
                    return null;
                }
                _sessionKey = claim.Value;
                ticket = await Options.SessionStore.RetrieveAsync(_sessionKey);
                if (ticket == null)
                {
                    Logger.LogWarning(@"Identity missing in session store");
                    return null;
                }
            }

            var currentUtc = Options.SystemClock.UtcNow;
            var issuedUtc = ticket.Properties.IssuedUtc;
            var expiresUtc = ticket.Properties.ExpiresUtc;

            if (expiresUtc != null && expiresUtc.Value < currentUtc)
            {
                if (Options.SessionStore != null)
                {
                    await Options.SessionStore.RemoveAsync(_sessionKey);
                }
                return null;
            }

            var allowRefresh = ticket.Properties.AllowRefresh ?? true;
            if (issuedUtc != null && expiresUtc != null && Options.SlidingExpiration && allowRefresh)
            {
                var timeElapsed = currentUtc.Subtract(issuedUtc.Value);
                var timeRemaining = expiresUtc.Value.Subtract(currentUtc);

                if (timeRemaining < timeElapsed)
                {
                    _shouldRenew = true;
                    _renewIssuedUtc = currentUtc;
                    var timeSpan = expiresUtc.Value.Subtract(issuedUtc.Value);
                    _renewExpiresUtc = currentUtc.Add(timeSpan);
                }
            }

            // Finally we have a valid ticket
            return ticket;
        }

        protected override async Task<AuthenticationTicket> HandleAuthenticateAsync()
        {
            AuthenticationTicket ticket = null;
            try
            {
                ticket = await EnsureCookieTicket();
                if (ticket == null)
                {
                    return null;
                }

                var context = new CookieValidatePrincipalContext(Context, ticket, Options); // this might ned change
                await Options.Notifications.ValidatePrincipal(context);

                if (context.Principal == null)
                {
                    return null;
                }

                if (context.ShouldRenew)
                {
                    _shouldRenew = true;
                }

                //return new AuthenticationTicket(context.Principal, context.Properties, Options.AuthenticationScheme);
                tenantResolver.Reset();
                return new AuthenticationTicket(context.Principal, context.Properties, tenantResolver.ResolveAuthScheme(Options.AuthenticationScheme));
            }
            catch (Exception exception)
            {
                var exceptionContext = new CookieExceptionContext(Context, Options,
                    CookieExceptionContext.ExceptionLocation.Authenticate, exception, ticket);

                Options.Notifications.Exception(exceptionContext);

                if (exceptionContext.Rethrow)
                {
                    throw;
                }
                return exceptionContext.Ticket;
            }
        }

        private CookieOptions BuildCookieOptions()
        {
            var cookieOptions = new CookieOptions
            {
                Domain = Options.CookieDomain,
                HttpOnly = Options.CookieHttpOnly,
                Path = Options.CookiePath ?? (OriginalPathBase.HasValue ? OriginalPathBase.ToString() : "/"),
                
            };
            if (Options.CookieSecure == CookieSecureOption.SameAsRequest)
            {
                cookieOptions.Secure = Request.IsHttps;
            }
            else
            {
                cookieOptions.Secure = Options.CookieSecure == CookieSecureOption.Always;
            }

            
            return cookieOptions;
        }

        protected override async Task FinishResponseAsync()
        {
            // Only renew if requested, and neither sign in or sign out was called
            if (!_shouldRenew || SignInAccepted || SignOutAccepted)
            {
                return;
            }

            var ticket = await HandleAuthenticateOnceAsync();
            try
            {
                if (_renewIssuedUtc.HasValue)
                {
                    ticket.Properties.IssuedUtc = _renewIssuedUtc;
                }
                if (_renewExpiresUtc.HasValue)
                {
                    ticket.Properties.ExpiresUtc = _renewExpiresUtc;
                }

                tenantResolver.Reset();

                if (Options.SessionStore != null && _sessionKey != null)
                {
                    await Options.SessionStore.RenewAsync(_sessionKey, ticket);

                    //var principal = new ClaimsPrincipal(
                    //    new ClaimsIdentity(
                    //        new[] { new Claim(SessionIdClaim, _sessionKey, ClaimValueTypes.String, Options.ClaimsIssuer) },
                    //        Options.AuthenticationScheme));
                    
                    var principal = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            new[] { new Claim(SessionIdClaim, _sessionKey, ClaimValueTypes.String, Options.ClaimsIssuer) },
                            tenantResolver.ResolveAuthScheme(Options.AuthenticationScheme)));

                    //ticket = new AuthenticationTicket(principal, null, Options.AuthenticationScheme);
                    ticket = new AuthenticationTicket(principal, null, tenantResolver.ResolveAuthScheme(Options.AuthenticationScheme));
                }

                //var cookieValue = Options.TicketDataFormat.Protect(ticket);
                var ticketDataFormat = GetTicketDataFormat();
                var cookieValue = ticketDataFormat.Protect(ticket);

                var cookieOptions = BuildCookieOptions();
                if (ticket.Properties.IsPersistent && _renewExpiresUtc.HasValue)
                {
                    cookieOptions.Expires = _renewExpiresUtc.Value.ToUniversalTime().DateTime;
                }

                //Options.CookieManager.AppendResponseCookie(
                //    Context,
                //    Options.CookieName,
                //    cookieValue,
                //    cookieOptions);

                Options.CookieManager.AppendResponseCookie(
                    Context,
                    tenantResolver.ResolveCookieName(Options.CookieName),
                    cookieValue,
                    cookieOptions);

                ApplyHeaders();
            }
            catch (Exception exception)
            {
                var exceptionContext = new CookieExceptionContext(Context, Options,
                    CookieExceptionContext.ExceptionLocation.FinishResponse, exception, ticket);
                Options.Notifications.Exception(exceptionContext);
                if (exceptionContext.Rethrow)
                {
                    throw;
                }
            }
        }

        protected override async Task HandleSignInAsync(SignInContext signin)
        {
            var ticket = await EnsureCookieTicket();
            try
            {
                tenantResolver.Reset();

                var cookieOptions = BuildCookieOptions();

                //var signInContext = new CookieResponseSignInContext(
                //    Context,
                //    Options,
                //    Options.AuthenticationScheme,
                //    signin.Principal,
                //    new AuthenticationProperties(signin.Properties),
                //    cookieOptions);

                var signInContext = new CookieResponseSignInContext(
                    Context,
                    Options,
                    tenantResolver.ResolveAuthScheme(Options.AuthenticationScheme),
                    signin.Principal,
                    new AuthenticationProperties(signin.Properties),
                    cookieOptions);

                DateTimeOffset issuedUtc;
                if (signInContext.Properties.IssuedUtc.HasValue)
                {
                    issuedUtc = signInContext.Properties.IssuedUtc.Value;
                }
                else
                {
                    issuedUtc = Options.SystemClock.UtcNow;
                    signInContext.Properties.IssuedUtc = issuedUtc;
                }

                if (!signInContext.Properties.ExpiresUtc.HasValue)
                {
                    signInContext.Properties.ExpiresUtc = issuedUtc.Add(Options.ExpireTimeSpan);
                }

                Options.Notifications.ResponseSignIn(signInContext);

                if (signInContext.Properties.IsPersistent)
                {
                    var expiresUtc = signInContext.Properties.ExpiresUtc ?? issuedUtc.Add(Options.ExpireTimeSpan);
                    signInContext.CookieOptions.Expires = expiresUtc.ToUniversalTime().DateTime;
                }

                ticket = new AuthenticationTicket(signInContext.Principal, signInContext.Properties, signInContext.AuthenticationScheme);
                if (Options.SessionStore != null)
                {
                    if (_sessionKey != null)
                    {
                        await Options.SessionStore.RemoveAsync(_sessionKey);
                    }
                    _sessionKey = await Options.SessionStore.StoreAsync(ticket);
                    var principal = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            new[] { new Claim(SessionIdClaim, _sessionKey, ClaimValueTypes.String, Options.ClaimsIssuer) },
                            Options.ClaimsIssuer));

                    //ticket = new AuthenticationTicket(principal, null, Options.AuthenticationScheme);
                    ticket = new AuthenticationTicket(principal, null, tenantResolver.ResolveAuthScheme(Options.AuthenticationScheme));
                }

                //var cookieValue = Options.TicketDataFormat.Protect(ticket);
                var ticketDataFormet = GetTicketDataFormat();
                var cookieValue = ticketDataFormet.Protect(ticket);

                //Options.CookieManager.AppendResponseCookie(
                //    Context,
                //    Options.CookieName,
                //    cookieValue,
                //    signInContext.CookieOptions);

                Options.CookieManager.AppendResponseCookie(
                    Context,
                    tenantResolver.ResolveCookieName(Options.CookieName),
                    cookieValue,
                    signInContext.CookieOptions);

                //var signedInContext = new CookieResponseSignedInContext(
                //    Context,
                //    Options,
                //    Options.AuthenticationScheme,
                //    signInContext.Principal,
                //    signInContext.Properties);

                var signedInContext = new CookieResponseSignedInContext(
                    Context,
                    Options,
                    tenantResolver.ResolveAuthScheme(Options.AuthenticationScheme),
                    signInContext.Principal,
                    signInContext.Properties);

                Options.Notifications.ResponseSignedIn(signedInContext);

                var shouldLoginRedirect = Options.LoginPath.HasValue && OriginalPath == Options.LoginPath;
                ApplyHeaders(shouldLoginRedirect);
            }
            catch (Exception exception)
            {
                var exceptionContext = new CookieExceptionContext(Context, Options,
                    CookieExceptionContext.ExceptionLocation.SignIn, exception, ticket);

                Options.Notifications.Exception(exceptionContext);
                if (exceptionContext.Rethrow)
                {
                    throw;
                }
            }
        }

        protected override async Task HandleSignOutAsync(SignOutContext signOutContext)
        {
            var ticket = await EnsureCookieTicket();
            try
            {
                tenantResolver.Reset();

                var cookieOptions = BuildCookieOptions();
                if (Options.SessionStore != null && _sessionKey != null)
                {
                    await Options.SessionStore.RemoveAsync(_sessionKey);
                }

                var context = new CookieResponseSignOutContext(
                    Context,
                    Options,
                    cookieOptions);

                Options.Notifications.ResponseSignOut(context);

                //Options.CookieManager.DeleteCookie(
                //    Context,
                //    Options.CookieName,
                //    context.CookieOptions);

                Options.CookieManager.DeleteCookie(
                    Context,
                    tenantResolver.ResolveCookieName(Options.CookieName),
                    context.CookieOptions);

                var shouldLogoutRedirect = Options.LogoutPath.HasValue && OriginalPath == Options.LogoutPath; //TODO: adjust


                ApplyHeaders(shouldLogoutRedirect);
            }
            catch (Exception exception)
            {
                var exceptionContext = new CookieExceptionContext(Context, Options,
                    CookieExceptionContext.ExceptionLocation.SignOut, exception, ticket);

                Options.Notifications.Exception(exceptionContext);
                if (exceptionContext.Rethrow)
                {
                    throw;
                }
            }
        }

        private void ApplyHeaders(bool shouldRedirectToReturnUrl = false)
        {
            Response.Headers.Set(HeaderNameCacheControl, HeaderValueNoCache);
            Response.Headers.Set(HeaderNamePragma, HeaderValueNoCache);
            Response.Headers.Set(HeaderNameExpires, HeaderValueMinusOne);

            if (shouldRedirectToReturnUrl && Response.StatusCode == 200)
            {
                var query = Request.Query;
                //var redirectUri = query.Get(Options.ReturnUrlParameter);
                tenantResolver.Reset();
                var redirectUri = query.Get(tenantResolver.ResolveReturnUrlParameter(Options.ReturnUrlParameter));

                if (!string.IsNullOrEmpty(redirectUri)
                    && IsHostRelative(redirectUri))
                {
                    var redirectContext = new CookieApplyRedirectContext(Context, Options, redirectUri);
                    Options.Notifications.ApplyRedirect(redirectContext);
                }
            }
        }

        private static bool IsHostRelative(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            if (path.Length == 1)
            {
                return path[0] == '/';
            }
            return path[0] == '/' && path[1] != '/' && path[1] != '\\';
        }

        protected override Task<bool> HandleForbiddenAsync(ChallengeContext context)
        {
            // HandleForbidden by redirecting to AccessDeniedPath if set
            if (!Options.AccessDeniedPath.HasValue)
            {
                return base.HandleForbiddenAsync(context);
            }

            try
            {
                var accessDeniedUri =
                    Request.Scheme +
                    "://" +
                    Request.Host +
                    OriginalPathBase +
                    Options.AccessDeniedPath; //TODO: probably need to resolve this per tenant as well

                var redirectContext = new CookieApplyRedirectContext(Context, Options, accessDeniedUri);
                Options.Notifications.ApplyRedirect(redirectContext);
            }
            catch (Exception exception)
            {
                var exceptionContext = new CookieExceptionContext(Context, Options,
                    CookieExceptionContext.ExceptionLocation.Forbidden, exception, ticket: null);

                Options.Notifications.Exception(exceptionContext);
                if (exceptionContext.Rethrow)
                {
                    throw;
                }
            }
            return Task.FromResult(true);
        }

        protected override Task<bool> HandleUnauthorizedAsync(ChallengeContext context)
        {
            if (!Options.LoginPath.HasValue)
            {
                return base.HandleUnauthorizedAsync(context);
            }

            var redirectUri = new AuthenticationProperties(context.Properties).RedirectUri;
            try
            {
                if (string.IsNullOrEmpty(redirectUri))
                {
                    redirectUri = OriginalPathBase + Request.Path + Request.QueryString;
                }

                //var loginUri = Options.LoginPath + QueryString.Create(Options.ReturnUrlParameter, redirectUri);
                tenantResolver.Reset();
                var loginUri = Options.LoginPath + QueryString.Create(tenantResolver.ResolveReturnUrlParameter(Options.ReturnUrlParameter), redirectUri);

                var redirectContext = new CookieApplyRedirectContext(Context, Options, BuildRedirectUri(loginUri));

                Options.Notifications.ApplyRedirect(redirectContext);
            }
            catch (Exception exception)
            {
                var exceptionContext = new CookieExceptionContext(Context, Options,
                    CookieExceptionContext.ExceptionLocation.Unauthorized, exception, ticket: null);
                Options.Notifications.Exception(exceptionContext);
                if (exceptionContext.Rethrow)
                {
                    throw;
                }
            }
            return Task.FromResult(true);
        }


    }
}
