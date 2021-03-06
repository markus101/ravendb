﻿using System;
using System.Net;
using System.Text.RegularExpressions;
using Raven.Database.Config;
using System.Linq;

namespace Raven.Database.Server.Security.Windows
{
	public class WindowsAuthConfigureHttpListener : IConfigureHttpListener
	{
		public static Regex IsAdminRequest = new Regex(@"(^/admin)|(^/databases/[\w.-_\d]+/admin)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

		private InMemoryRavenConfiguration configuration;
		public void Configure(HttpListener listener, InMemoryRavenConfiguration config)
		{
			configuration = config;
			listener.AuthenticationSchemes = AuthenticationSchemes.IntegratedWindowsAuthentication |
												 AuthenticationSchemes.Anonymous;
			
			listener.AuthenticationSchemeSelectorDelegate += AuthenticationSchemeSelectorDelegate;
		}

		private AuthenticationSchemes AuthenticationSchemeSelectorDelegate(HttpListenerRequest request)
		{
			var authHeader = request.Headers["Authorization"];
			var hasApiKey = "True".Equals(request.Headers["Has-Api-Key"], StringComparison.CurrentCultureIgnoreCase);
			if(string.IsNullOrEmpty(authHeader) == false && authHeader.StartsWith("Bearer ") || hasApiKey)
			{
				// this is an OAuth request that has a token
				// we allow this to go through and we will authenticate that on the OAuth Request Authorizer
				return AuthenticationSchemes.Anonymous;
			}
			if (NeverSecret.Urls.Contains(request.Url.AbsolutePath))
				return AuthenticationSchemes.Anonymous;
					
			if (IsAdminRequest.IsMatch(request.RawUrl))
				return AuthenticationSchemes.IntegratedWindowsAuthentication;

			switch (configuration.AnonymousUserAccessMode)
			{
				case AnonymousUserAccessMode.All:
					return AuthenticationSchemes.Anonymous;
				case AnonymousUserAccessMode.Get:
					return AbstractRequestAuthorizer.IsGetRequest(request.HttpMethod, request.Url.AbsolutePath) ?
						AuthenticationSchemes.Anonymous | AuthenticationSchemes.IntegratedWindowsAuthentication :
						AuthenticationSchemes.IntegratedWindowsAuthentication;
				case AnonymousUserAccessMode.None:
					return AuthenticationSchemes.IntegratedWindowsAuthentication;
				default:
					throw new ArgumentException(string.Format("Cannot understand access mode: '{0}'", configuration.AnonymousUserAccessMode));
			}
		}
	}
}