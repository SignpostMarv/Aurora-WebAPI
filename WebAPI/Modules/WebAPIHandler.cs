/*
 * Copyright (c) Contributors, http://aurora-sim.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the Aurora-Sim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Drawing;
using System.Drawing.Text;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Reflection;
using System.Timers;
using System.Web;

using BitmapProcessing;

using Nini.Config;

using OpenMetaverse;
using OpenMetaverse.Imaging;
using OpenMetaverse.StructuredData;
using EventFlags = OpenMetaverse.DirectoryManager.EventFlags;

using OpenSim.Services.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.Framework.Interfaces;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;
using GridRegion = OpenSim.Services.Interfaces.GridRegion;

using Aurora.Framework;
using Aurora.Framework.Servers.HttpServer;
using Aurora.DataManager;
using Aurora.Services.DataService;
using Aurora.Simulation.Base;
using RegionFlags = Aurora.Framework.RegionFlags;

namespace Aurora.Services
{
    public enum WebAPIHttpMethod
    {
        GET,
        POST
    }

    public class WebAPIMethod : Attribute
    {
        private WebAPIHttpMethod m_httpMethod;
        public WebAPIHttpMethod HttpMethod
        {
            get
            {
                return m_httpMethod;
            }
        }

        private bool m_passOnRequestingAgentID;
        public bool PassOnRequestingAgentID
        {
            get
            {
                return m_passOnRequestingAgentID;
            }
        }

        public WebAPIMethod(WebAPIHttpMethod HttpMethod)
        {
            m_httpMethod = HttpMethod;
            m_passOnRequestingAgentID = false;
        }

        public WebAPIMethod(WebAPIHttpMethod HttpMethod, bool passOnRequestingAgentID)
        {
            m_httpMethod = HttpMethod;
            m_passOnRequestingAgentID = passOnRequestingAgentID;
        }

    }

    public interface IWebAPIConnector
    {
        bool Enabled { get; }

        string Handler { get; }

        uint HandlerPort { get; }

        uint TexturePort { get; }

        /// <summary>
        /// Log the user's access to the API. Will be used for throttling.
        /// </summary>
        /// <param name="user"></param>
        /// <param name="method"></param>
        /// <returns>indicates whether the logging was successful</returns>
        bool LogAPICall(UUID user, string method);

        /// <summary>
        /// Clears the API access log
        /// </summary>
        /// <param name="staleOnly">if true only clears logs older than an hour, if false clears entire log</param>
        /// <returns>indicates whether the action was successful</returns>
        bool ClearLog(bool staleOnly);

        /// <summary>
        /// Determines if the specified user can access the specified method.
        /// </summary>
        /// <param name="user"></param>
        /// <param name="method"></param>
        /// <returns></returns>
        bool AllowAPICall(UUID user, string method);

        /// <summary>
        /// Changes the specified user's API access rate
        /// </summary>
        /// <param name="user">if UUID.Zero, sets the default access permissions for all users</param>
        /// <param name="method">if empty, sets the default rate for all methods for the specified user</param>
        /// <param name="rate">per-hour rate limit. if null, prevents the specified user from using the specified method. if zero, access is not rate limited.</param>
        /// <returns></returns>
        bool ChangeRateLimit(UUID user, string method, uint? rate);

        /// <summary>
        /// Prevents the specified user from having any access to the API
        /// </summary>
        /// <param name="user"></param>
        /// <returns></returns>
        bool RevokeAPIAccess(UUID user);

        /// <summary>
        /// Removes all custom access rates for the specified user
        /// </summary>
        /// <param name="user"></param>
        /// <returns></returns>
        bool ResetAPIAccess(UUID user);

        /// <summary>
        /// Gets the specified user's API access rate limit
        /// </summary>
        /// <param name="user"></param>
        /// <param name="method"></param>
        /// <returns>null indicates the user does not have permission to use the specified method.</returns>
        uint? GetRateLimit(UUID user, string method);

        /// <summary>
        /// Gets the usage of the specified method by the specified user within the last hour.
        /// </summary>
        /// <param name="user"></param>
        /// <param name="method"></param>
        /// <returns></returns>
        uint GetUsageRate(UUID user, string method);

        /// <summary>
        /// Determines if the specified user has exceeded their rate limit.
        /// </summary>
        /// <param name="user"></param>
        /// <param name="method"></param>
        /// <returns></returns>
        bool RateLimitExceed(UUID user, string method);

        /// <summary>
        /// Gets current access token for the specified user.
        /// </summary>
        /// <param name="user"></param>
        /// <returns></returns>
        UUID GetAccessToken(UUID user);

        /// <summary>
        /// Gets a new access token for the specified user. 
        /// </summary>
        /// <param name="user"></param>
        /// <returns></returns>
        UUID GetNewAccessToken(UUID user);
    }

    public interface IWebAPIHandler
    {
        /// <summary>
        /// Determines if a given API call should be processed.
        /// </summary>
        /// <param name="method"></param>
        /// <param name="request"></param>
        /// <param name="response">If request lacks the authorization headers, response is modified to indicate that http auth should be performed</param>
        /// <returns>true if the API call should be allowed, false otherwise.</returns>
        bool AllowAPICall(string method, OSHttpRequest request, OSHttpResponse response);

        byte[] doAPICall(BaseStreamHandler caller, string path, Stream requestData, OSHttpRequest httpRequest, OSHttpResponse httpResponse);

        Dictionary<WebAPIHttpMethod, List<string>> APIMethods();
    }

    public class WebAPI_StreamHandler : BaseStreamHandler
    {
        const string httpPath = "/webapi";

        protected IWebAPIHandler WebAPI;

        public WebAPI_StreamHandler(WebAPIHandler webapi, WebAPIHttpMethod method)
            : base(method.ToString(), httpPath)
        {
            WebAPI = webapi;
        }

        #region BaseStreamHandler

        public override byte[] Handle(string path, Stream requestData, OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            return WebAPI.doAPICall(this, path, requestData, httpRequest, httpResponse);
        }

        #endregion
    }

    public class WebAPIConnector : IAuroraDataPlugin, IWebAPIConnector
    {
        private bool m_enabled = false;
        public bool Enabled
        {
            get { return m_enabled; }
        }

        private string m_Handler = string.Empty;
        public string Handler
        {
            get
            {
                return m_Handler;
            }
        }

        private uint m_HandlerPort = 0;
        public uint HandlerPort
        {
            get
            {
                return m_HandlerPort;
            }
        }

        private uint m_TexturePort = 0;
        public uint TexturePort
        {
            get
            {
                return m_TexturePort;
            }
        }

        private IGenericData GD;
        private string m_connectionString;

        private const string c_table_access = "webapi_access";
        private const string c_table_accessLog = "webapi_access_log";
        private const string c_table_accessTokens = "webapi_access_tokens";

        private Dictionary<string, uint?> defaultAccessRate = new Dictionary<string, uint?>();

        #region console wrappers

        private void Info(object message)
        {
            MainConsole.Instance.Info("[" + Name + "]: " + message.ToString());
        }

        private void Warn(object message)
        {
            MainConsole.Instance.Warn("[" + Name + "]: " + message.ToString());
        }

        #endregion

        #region IAuroraDataPlugin Members

        public string Name
        {
            get
            {
                return "WebAPIConnector";
            }
        }

        private bool handleConfig(IConfigSource m_config, string defaultConnectionString)
        {
            IConfig config = m_config.Configs["WebAPI"];
            if (config == null)
            {
                m_enabled = false;
                Warn("not loaded, no configuration found.");
                return false;
            }

            m_Handler = config.GetString("Handler", string.Empty);
            m_HandlerPort = config.GetUInt("Port", 0);
            m_TexturePort = config.GetUInt("TextureServerPort", 0);
            m_connectionString = config.GetString("ConnectionString", defaultConnectionString);

            if (Handler == string.Empty || HandlerPort == 0 || TexturePort == 0)
            {
                m_enabled = false;
                Warn("Not loaded, configuration missing.");
                return false;
            }

            m_enabled = true;
            return true;
        }

        public void Initialize(IGenericData GenericData, IConfigSource source, IRegistryCore simBase, string defaultConnectionString)
        {
            if (handleConfig(source, defaultConnectionString))
            {
                if (!Enabled)
                {
                    Warn("not loaded, disabled in config.");
                }
                else
                {
                    GD = GenericData;
                    GD.ConnectToDatabase(m_connectionString, "WebAPI", true);

                    QueryFilter filter = new QueryFilter();
                    filter.andFilters["user"] = UUID.Zero;
                    List<string> query = GD.Query(new string[2]{
                        "method",
                        "rate"
                    }, c_table_access, filter, null, null, null);
                    if (query.Count % 2 == 0)
                    {
                        Dictionary<string, uint?> DAR = new Dictionary<string, uint?>();
                        for (int i = 0; i < query.Count; i += 2)
                        {
                            if (string.IsNullOrEmpty(query[i + 1]))
                            {
                                DAR[query[i]] = null;
                            }
                            else
                            {
                                DAR[query[i]] = uint.Parse(query[i + 1]);
                            }
                        }
                        DataManager.DataManager.RegisterPlugin(this);
                    }
                    else
                    {
                        MainConsole.Instance.Error("[" + Name + "]: Could not find default access rate limits");
                    }

                }
            }
        }

        #endregion

        #region IWebAPIConnector Members

        public bool LogAPICall(UUID user, string method)
        {
            DateTime now = DateTime.Now;
            uint ut = Utils.DateTimeToUnixTime(now);
            DateTime origin = new DateTime(1970, 1, 1, 0, 0, 0);
            origin.AddSeconds(ut);
            return GD.Insert(c_table_accessLog, new object[3]{
                user,
                method,
                ut + (((now.Ticks - origin.Ticks) / 10000000.0) % 1)
            });
        }

        public bool ClearLog(bool staleOnly)
        {
            QueryFilter filter = new QueryFilter();
            if (staleOnly)
            {
                DateTime now = DateTime.Now;
                uint ut = Utils.DateTimeToUnixTime(now);
                DateTime origin = new DateTime(1970, 1, 1, 0, 0, 0);
                origin.AddSeconds(ut);
                double staleTime = ut + (((now.Ticks - origin.Ticks) / 10000000.0) % 1) - 3600;
                filter.andLessThanEqFilters["loggedat"] = staleTime;
            }

            return GD.Delete(c_table_accessLog, staleOnly ? filter : null);
        }

        public bool AllowAPICall(UUID user, string method)
        {
            uint? rateLimit = GetRateLimit(user, method);
            return rateLimit.HasValue && (rateLimit.Value > 0 ? GetUsageRate(user, method) <= rateLimit.Value : true);
        }

        public bool ChangeRateLimit(UUID user, string method, uint? rate){
            return GD.Insert(c_table_access, new object[3]{
                user,
                method.Trim(),
                rate
            }, "rate", rate);
        }

        public bool RevokeAPIAccess(UUID user)
        {
            QueryFilter filter = new QueryFilter();
            filter.andFilters["user"] = user;

            GD.Delete(c_table_access, filter);
            return ChangeRateLimit(user, "", null);
        }

        public bool ResetAPIAccess(UUID user)
        {
            if (user != UUID.Zero)
            {
                QueryFilter filter = new QueryFilter();
                filter.andFilters["user"] = user;

                GD.Delete(c_table_access, filter);
            }
            return false;
        }

        public uint? GetRateLimit(UUID user, string method)
        {
            method = method.Trim();
            QueryFilter filter = new QueryFilter();
            filter.andFilters["user"] = user;
            filter.andFilters["method"] = method;
            List<string> query = GD.Query(new string[1] { "rate" }, c_table_access, filter, null, 0, 1);
            if (query.Count < 1 && method != string.Empty)
            {
                filter.andFilters["method"] = "";
                query = GD.Query(new string[1] { "rate" }, c_table_access, filter, null, 0, 1);
            }
            if (query.Count < 1)
            {
                return defaultAccessRate.ContainsKey(method) ? defaultAccessRate[method] : (defaultAccessRate.ContainsKey("") ? defaultAccessRate[""] : null);
            }
            else if (string.IsNullOrEmpty(query[0]))
            {
                return null;
            }
            else
            {
                return uint.Parse(query[0]);
            }
        }

        public uint GetUsageRate(UUID user, string method)
        {
            DateTime now = DateTime.Now;
            uint ut = Utils.DateTimeToUnixTime(now);
            DateTime origin = new DateTime(1970, 1, 1, 0, 0, 0);
            origin.AddSeconds(ut);
            double staleTime = ut + (((now.Ticks - origin.Ticks) / 10000000.0) % 1) - 3600;

            method = method.Trim();
            QueryFilter filter = new QueryFilter();
            filter.andFilters["user"] = user;
            filter.andFilters["method"] = method;
            filter.andGreaterThanEqFilters["loggedat"] = staleTime;

            List<string> query = GD.Query(new string[1] { "COUNT(*)" }, c_table_accessLog, filter, null, 0, 1);
            return uint.Parse(query[0]);
        }

        public bool RateLimitExceed(UUID user, string method)
        {
            uint? rateLimit = GetRateLimit(user, method);
            return !rateLimit.HasValue || rateLimit.Value <= GetUsageRate(user, method);
        }

        public UUID GetAccessToken(UUID user)
        {
            QueryFilter filter = new QueryFilter();
            filter.andFilters["user"] = user;
            List<string> query = GD.Query(new string[1] { "accessToken" }, c_table_accessTokens, filter, null, 0, 1);

            return query.Count < 1 ? GetNewAccessToken(user) : UUID.Parse(query[0]);
        }

        public UUID GetNewAccessToken(UUID user)
        {
            QueryFilter filter = new QueryFilter();
            filter.andFilters["user"] = user;
            List<string> query = GD.Query(new string[1] { "accessToken" }, c_table_accessTokens, filter, null, 0, 1);

            UUID newToken = UUID.Random();
            if (query.Count < 1)
            {
                GD.Insert(c_table_accessTokens, new string[2] { user.ToString(), newToken.ToString() });
            }
            else
            {
                Dictionary<string, object> update = new Dictionary<string,object>(1);
                update["accessToken"] = newToken;
                GD.Update(c_table_accessTokens, update, null, filter, 0, 1);
            }
            return newToken;
        }

        #endregion
    }

    public class WebAPIHandler : IService, IWebAPIHandler
    {
        private IWebAPIConnector m_connector;

        private IHttpServer m_server = null;
        private IHttpServer m_server2 = null;
        string m_servernick = "Aurora-Sim";
        protected IRegistryCore m_registry;
        protected OSDMap m_GridInfo;

        protected UUID AdminAgentID = UUID.Zero;

        public string Name
        {
            get { return GetType().Name; }
        }

        #region IService

        public void Initialize(IConfigSource config, IRegistryCore registry)
        {
        }

        public void Start(IConfigSource config, IRegistryCore registry)
        {
            m_connector = DataManager.DataManager.RequestPlugin<WebAPIConnector>();
            if (m_connector == null || m_connector.Enabled == false || m_connector.Handler != Name)
            {
                return;
            }

            IConfig handlerConfig = config.Configs["Handlers"];
            UUID.TryParse(handlerConfig.GetString("WebAPIAdminID", UUID.Zero.ToString()), out AdminAgentID);

            if (m_connector.Handler != Name)
            {
                MainConsole.Instance.Warn("[WebAPI]: module not loaded");
                return;
            }
            MainConsole.Instance.Info("[WebAPI]: module loaded");

            m_registry = registry;

            IConfig GridInfoConfig = config.Configs["GridInfoService"];
            if (GridInfoConfig != null)
            {
                m_servernick = GridInfoConfig.GetString("gridnick", m_servernick);
            }

            m_GridInfo = new OSDMap();
            if (GridInfoConfig != null && (GridInfoConfig.GetString("gridname", "") != "" && GridInfoConfig.GetString("gridnick", "") != ""))
            {
                foreach (string k in GridInfoConfig.GetKeys())
                {
                    m_GridInfo[k] = GridInfoConfig.GetString(k);
                }
            }

            ISimulationBase simBase = registry.RequestModuleInterface<ISimulationBase>();

            m_server = simBase.GetHttpServer(handlerConfig.GetUInt(Name + "Port", m_connector.HandlerPort));
            foreach (WebAPIHttpMethod method in Enum.GetValues(typeof(WebAPIHttpMethod)))
            {
                m_server.AddStreamHandler(new WebAPI_StreamHandler(this, method)); // This handler is for WebAPI methods that only read data
            }

            m_server2 = simBase.GetHttpServer(handlerConfig.GetUInt(Name + "TextureServerPort", m_connector.TexturePort));
            m_server2.AddHTTPHandler("GridTexture", OnHTTPGetTextureImage);

            m_GridInfo[Name + "TextureServer"] = m_server2.ServerURI;

            m_authNonces = new ExpiringCache<string, string>();

            MainConsole.Instance.Commands.AddCommand("webapi promote user", "Grants the specified user administrative powers within WebAPI.", "webapi promote user", PromoteUser);
            MainConsole.Instance.Commands.AddCommand("webapi demote user", "Revokes administrative powers for WebAPI from the specified user.", "webapi demote user", DemoteUser);
            MainConsole.Instance.Commands.AddCommand("webapi add group as news source", "Sets a group as a news source so in-world group notices can be used as a publishing tool for the website.", "webapi add group as news source", AddGroupAsNewsSource);
            MainConsole.Instance.Commands.AddCommand("webapi remove group as news source", "Removes a group as a news source so it's notices will stop showing up on the news page.", "webapi remove group as news source", RemoveGroupAsNewsSource);
            MainConsole.Instance.Commands.AddCommand("webapi list methods", "List API methods", "webapi list methods", ListAPImethods);
            MainConsole.Instance.Commands.AddCommand("webapi get access token", "Gets the current access token to the API for the specified user", "webapi get access token", GetAccessToken);
            MainConsole.Instance.Commands.AddCommand("webapi get new access token", "Gets a new access token to the API for the specified user", "webapi get new access token", GetNewAccessToken);
            MainConsole.Instance.Commands.AddCommand("webapi clear log", "Clears the API access log", "webapi clear log [staleonly]", ClearLog);
            MainConsole.Instance.Commands.AddCommand("webapi get usage rate", "Get the current usage rate for the specified user on the specified method.", "webapi get usage rate", GetUsageRate);
            MainConsole.Instance.Commands.AddCommand("webapi grant access", "Grants access to a specified method for a specified user.", "webapi grant access [method]", GrantAPIAccess);
            MainConsole.Instance.Commands.AddCommand("webapi revoke access", "Revokes access for a specified user.", "webapi revoke access", RevokeAPIAccess);
            MainConsole.Instance.Commands.AddCommand("webapi reset access", "Resets access to defaults for a specified method for a specified user.", "webapi reset access", ResetAPIAccess);
        }

        public void FinishedStartup()
        {
        }

        #endregion

        #region Console Commands

        #region WebAPI access control

        private void ListAPImethods(string[] cmd)
        {
            string resp = "API methods:";
            foreach (KeyValuePair<WebAPIHttpMethod, List<string>> kvp in APIMethods())
            {
                resp += "\n" + "[" + kvp.Key.ToString() + "]";
                foreach (string method in kvp.Value)
                {
                    resp += "\n\t" + method;
                }
            }
            MainConsole.Instance.Info("[" + Name + "]: " + resp);
        }

        private void GetAccessToken(string[] cmd)
        {
            string name = MainConsole.Instance.Prompt("Name of user");

            OSDMap args = new OSDMap(1);
            args["Name"] = name;
            OSDMap resp = CheckIfUserExists(args);
            if ((!resp.ContainsKey("Verified") || !resp.ContainsKey("UUID")) || (!resp["Verified"].AsBoolean() || resp["UUID"].AsUUID() == UUID.Zero))
            {
                MainConsole.Instance.ErrorFormat("[" + Name + "]: {0} does not appear to exist.", name);
            }
            else
            {
                MainConsole.Instance.InfoFormat("[" + Name + "]: Access token for {0} : {1}", name, m_connector.GetAccessToken(resp["UUID"].AsUUID()));
            }
        }

        private void GetNewAccessToken(string[] cmd)
        {
            string name = MainConsole.Instance.Prompt("Name of user");

            OSDMap args = new OSDMap(1);
            args["Name"] = name;
            OSDMap resp = CheckIfUserExists(args);
            if ((!resp.ContainsKey("Verified") || !resp.ContainsKey("UUID")) || (!resp["Verified"].AsBoolean() || resp["UUID"].AsUUID() == UUID.Zero))
            {
                MainConsole.Instance.ErrorFormat("[" + Name + "]: {0} does not appear to exist.", name);
            }
            else
            {
                MainConsole.Instance.InfoFormat("[" + Name + "]: Access token for {0} : {1}", name, m_connector.GetNewAccessToken(resp["UUID"].AsUUID()));
            }
        }

        private void ClearLog(string[] cmd)
        {
            m_connector.ClearLog(cmd.Length == 4 && cmd[3] == "staleonly");
        }

        private void GetUsageRate(string[] cmd)
        {
            string name = MainConsole.Instance.Prompt("Name of user");
            string method = MainConsole.Instance.Prompt("Name of method");

            OSDMap args = new OSDMap(1);
            args["Name"] = name;
            OSDMap resp = CheckIfUserExists(args);
            if ((!resp.ContainsKey("Verified") || !resp.ContainsKey("UUID")) || (!resp["Verified"].AsBoolean() || resp["UUID"].AsUUID() == UUID.Zero))
            {
                MainConsole.Instance.ErrorFormat("[" + Name + "]: {0} does not appear to exist.", name);
            }
            else
            {
                MainConsole.Instance.InfoFormat("[" + Name + "]: Current usage rate for {0} on method {1} : {2}", name, method, m_connector.GetUsageRate(resp["UUID"].AsUUID(), method));
            }
        }

        private void GrantAPIAccess(string[] cmd)
        {
            string name = MainConsole.Instance.Prompt("Name of user");

            OSDMap args = new OSDMap(1);
            args["Name"] = name;
            OSDMap resp = CheckIfUserExists(args);
            if ((!resp.ContainsKey("Verified") || !resp.ContainsKey("UUID")) || (!resp["Verified"].AsBoolean() || resp["UUID"].AsUUID() == UUID.Zero))
            {
                MainConsole.Instance.ErrorFormat("[" + Name + "]: {0} does not appear to exist.", name);
            }
            else
            {
                string method = MainConsole.Instance.Prompt("Name of method (leave blank to set default rate for all methods)", cmd.Length == 4 ? cmd[3].Trim() : "");
                uint rate = uint.Parse(MainConsole.Instance.Prompt("Hourly rate limit (zero for no limit)", "0"));
                m_connector.ChangeRateLimit(resp["UUID"].AsUUID(), method, rate);
            }
        }

        private void RevokeAPIAccess(string[] cmd)
        {
            string name = MainConsole.Instance.Prompt("Name of user");

            OSDMap args = new OSDMap(1);
            args["Name"] = name;
            OSDMap resp = CheckIfUserExists(args);
            if ((!resp.ContainsKey("Verified") || !resp.ContainsKey("UUID")) || (!resp["Verified"].AsBoolean() || resp["UUID"].AsUUID() == UUID.Zero))
            {
                MainConsole.Instance.ErrorFormat("[" + Name + "]: {0} does not appear to exist.", name);
            }
            else
            {
                m_connector.RevokeAPIAccess(resp["UUID"].AsUUID());
            }
        }

        private void ResetAPIAccess(string[] cmd)
        {
            string name = MainConsole.Instance.Prompt("Name of user");

            OSDMap args = new OSDMap(1);
            args["Name"] = name;
            OSDMap resp = CheckIfUserExists(args);
            if ((!resp.ContainsKey("Verified") || !resp.ContainsKey("UUID")) || (!resp["Verified"].AsBoolean() || resp["UUID"].AsUUID() == UUID.Zero))
            {
                MainConsole.Instance.ErrorFormat("[" + Name + "]: {0} does not appear to exist.", name);
            }
            else
            {
                m_connector.ResetAPIAccess(resp["UUID"].AsUUID());
            }
        }

        #endregion

        #region WebAPI Admin

        private void PromoteUser (string[] cmd)
        {
            string name = MainConsole.Instance.Prompt ("Name of user");
            UserAccount acc = m_registry.RequestModuleInterface<IUserAccountService> ().GetUserAccount (UUID.Zero, name);
            if (acc == null)
            {
                MainConsole.Instance.Warn ("You must create the user before promoting them.");
                return;
            }
            IAgentConnector agents = Aurora.DataManager.DataManager.RequestPlugin<IAgentConnector>();
            if (agents == null)
            {
                MainConsole.Instance.Warn("Could not get IAgentConnector plugin");
                return;
            }
            IAgentInfo agent = agents.GetAgent(acc.PrincipalID);
            if (agent == null)
            {
                MainConsole.Instance.Warn("Could not get IAgentInfo for " + name + ", try logging the user into your grid first.");
                return;
            }
            agent.OtherAgentInformation["WebUIEnabled"] = true;
            Aurora.DataManager.DataManager.RequestPlugin<IAgentConnector> ().UpdateAgent (agent);
            MainConsole.Instance.Warn ("Admin added");
        }

        private void DemoteUser (string[] cmd)
        {
            string name = MainConsole.Instance.Prompt ("Name of user");
            UserAccount acc = m_registry.RequestModuleInterface<IUserAccountService> ().GetUserAccount (UUID.Zero, name);
            if (acc == null)
            {
                MainConsole.Instance.Warn ("User does not exist, no action taken.");
                return;
            }
            IAgentConnector agents = Aurora.DataManager.DataManager.RequestPlugin<IAgentConnector>();
            if (agents == null)
            {
                MainConsole.Instance.Warn("Could not get IAgentConnector plugin");
                return;
            }
            IAgentInfo agent = agents.GetAgent(acc.PrincipalID);
            if (agent == null)
            {
                MainConsole.Instance.Warn("Could not get IAgentInfo for " + name + ", try logging the user into your grid first.");
                return;
            }
            agent.OtherAgentInformation["WebUIEnabled"] = false;
            Aurora.DataManager.DataManager.RequestPlugin<IAgentConnector> ().UpdateAgent (agent);
            MainConsole.Instance.Warn ("Admin removed");
        }

        #endregion

        #region Groups

        private void AddGroupAsNewsSource(string[] cmd)
        {
            string name = MainConsole.Instance.Prompt("Name of group");
            GroupRecord group = Aurora.DataManager.DataManager.RequestPlugin<IGroupsServiceConnector>().GetGroupRecord(AdminAgentID, UUID.Zero, name);
            if (group == null)
            {
                MainConsole.Instance.Warn("[WebAPI] You must create the group before adding it as a news source");
                return;
            }
            IGenericsConnector generics = Aurora.DataManager.DataManager.RequestPlugin<IGenericsConnector>();
            OSDMap useValue = new OSDMap();
            useValue["Use"] = OSD.FromBoolean(true);
            generics.AddGeneric(group.GroupID, "Group", "WebAPI_newsSource", useValue);
            MainConsole.Instance.Warn(string.Format("[WebAPI]: \"{0}\" was added as a news source", group.GroupName));
        }

        private void RemoveGroupAsNewsSource(string[] cmd)
        {
            string name = MainConsole.Instance.Prompt("Name of group");
            GroupRecord group = Aurora.DataManager.DataManager.RequestPlugin<IGroupsServiceConnector>().GetGroupRecord(AdminAgentID, UUID.Zero, name);
            if (group == null)
            {
                MainConsole.Instance.Warn(string.Format("[WebAPI] \"{0}\" did not appear to be a Group, cannot remove as news source", name));
                return;
            }
            IGenericsConnector generics = Aurora.DataManager.DataManager.RequestPlugin<IGenericsConnector>();
            generics.RemoveGeneric(group.GroupID, "Group", "WebUI_newsSource");
            MainConsole.Instance.Warn(string.Format("[WebAPI]: \"{0}\" was removed as a news source", group.GroupName));
        }

        #endregion

        #endregion

        #region IWebAPIHandler members

        private Dictionary<WebAPIHttpMethod, Dictionary<string, MethodInfo>> m_APIMethods = new Dictionary<WebAPIHttpMethod, Dictionary<string, MethodInfo>>();

        public Dictionary<WebAPIHttpMethod, List<string>> APIMethods()
        {
            Dictionary<WebAPIHttpMethod, List<string>> methods = new Dictionary<WebAPIHttpMethod, List<string>>(m_APIMethods.Count);
            foreach (KeyValuePair<WebAPIHttpMethod, Dictionary<string, MethodInfo>> kvp in m_APIMethods)
            {
                methods[kvp.Key] = new List<string>(kvp.Value.Keys);
            }
            return methods;
        }

        public WebAPIHandler()
        {
            m_APIMethods[WebAPIHttpMethod.GET] = new Dictionary<string,MethodInfo>();
            m_APIMethods[WebAPIHttpMethod.POST] = new Dictionary<string,MethodInfo>();

            MethodInfo[] methods = this.GetType().GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            for (uint i = 0; i < methods.Length; ++i)
            {
                WebAPIMethod attr = (WebAPIMethod)Attribute.GetCustomAttribute(methods[i], typeof(WebAPIMethod));
                if (attr != null && methods[i].ReturnType == typeof(OSDMap) && methods[i].GetParameters().Length >= 1 && methods[i].GetParameters()[0].ParameterType == typeof(OSDMap))
                {
                    if ((!attr.PassOnRequestingAgentID && methods[i].GetParameters().Length == 1) || (attr.PassOnRequestingAgentID && methods[i].GetParameters().Length == 2))
                    {
                        m_APIMethods[attr.HttpMethod][methods[i].Name] = methods[i];
                    }
                }
            }
        }

        private ExpiringCache<string, string> m_authNonces;

        private Dictionary<string, string> authorizationHeader(OSHttpRequest request)
        {
            if ((new List<string>(request.Headers.AllKeys)).Contains("authorization"))
            {
                string auth = request.Headers["authorization"];
                if (auth.Substring(0, 7) == "Digest ")
                {
                    string[] authBits = Regex.Split(auth.Substring(7), ", ");
                    Dictionary<string, string> authorization = new Dictionary<string, string>(authBits.Length);
                    Regex authBitRegex = new Regex("^\".+\"$");
                    foreach (string authBit in authBits)
                    {
                        int pos = authBit.IndexOf('=');
                        if (pos >= 0)
                        {
                            authorization[authBit.Substring(0, pos)] = authBitRegex.IsMatch(authBit.Substring(pos + 1)) ? authBit.Substring(pos + 2, authBit.Length - pos - 3) : authBit.Substring(pos + 1);
                        }
                    }
                    return authorization;
                }
            }
            return null;
        }

        private UUID authUser(OSHttpRequest request)
        {
            Dictionary<string, string> authorization = authorizationHeader(request);
            if (authorization != null && authorization.ContainsKey("username"))
            {
                OSDMap args = new OSDMap(1);
                args["Name"] = authorization["username"];
                OSDMap resp = CheckIfUserExists(args);
                return ((!resp.ContainsKey("Verified") || !resp.ContainsKey("UUID")) || (!resp["Verified"].AsBoolean() || resp["UUID"].AsUUID() == UUID.Zero)) ? UUID.Zero : resp["UUID"].AsUUID();
            }
            return UUID.Zero;
        }

        public bool AllowAPICall(string method, OSHttpRequest request, OSHttpResponse response)
        {
            Dictionary<string, string> authorization = authorizationHeader(request);
            if (authorization != null)
            {
                string storednonce;
                if (
                    authorization.ContainsKey("username") &&
                    authorization.ContainsKey("realm") &&
                    authorization.ContainsKey("uri") &&
                    authorization.ContainsKey("qop") &&
                    authorization.ContainsKey("nonce") &&
                    authorization.ContainsKey("nc") &&
                    authorization.ContainsKey("cnonce") &&
                    authorization.ContainsKey("opaque") &&
                    m_authNonces.TryGetValue(authorization["opaque"], out storednonce) &&
                    authorization["nonce"] == storednonce
                )
                {
                    m_authNonces.Remove(authorization["opaque"]);

                    UUID accountID = authUser(request);
                    if (accountID == UUID.Zero)
                    {
                        response.StatusCode = 403;
                        response.StatusDescription = "Forbidden";
                        MainConsole.Instance.DebugFormat("[WebAPI]: {0} is not permitted to use WebAPI", authorization["username"], method);
                    }
                    else
                    {
                        string password = Utils.MD5String(m_connector.GetAccessToken(accountID).ToString());

                        string HA1 = Util.Md5Hash(string.Join(":", new string[]{
                                authorization["username"],
                                Name,
                                password
                            }));
                        string HA2 = Util.Md5Hash(request.HttpMethod + ":" + authorization["uri"]);
                        string expectedDigestResponse = (authorization.ContainsKey("qop") && authorization["qop"] == "auth") ? Util.Md5Hash(string.Join(":", new string[]{
                                HA1,
                                storednonce,
                                authorization["nc"],
                                authorization["cnonce"],
                                "auth",
                                HA2
                            })) : Util.Md5Hash(string.Join(":", new string[]{
                                HA1,
                                storednonce,
                                HA2
                            }));
                        if (expectedDigestResponse == authorization["response"])
                        {
                            if (m_connector.AllowAPICall(accountID, method))
                            {
                                m_connector.LogAPICall(accountID, method);
                                return true;
                            }
                            else if (m_connector.GetRateLimit(accountID, method) == null)
                            {
                                response.StatusCode = 403;
                                response.StatusDescription = "Forbidden";
                                MainConsole.Instance.DebugFormat("[WebAPI]: {0} is not permitted to use API method {1}", authorization["username"], method);
                            }
                            else if (m_connector.RateLimitExceed(accountID, method))
                            {
                                response.StatusCode = 429;
                                response.StatusDescription = "Too Many Requests";
                                MainConsole.Instance.DebugFormat("[WebAPI]: {0} exceeded their hourly rate limit for API method {1}", authorization["username"], method);
                            }
                            else
                            {
                                response.StatusCode = 500;
                                MainConsole.Instance.DebugFormat("[WebAPI]: {0} cannotuse API method {1}, although we're not sure why.", authorization["username"], method);
                            }
                        }
                        else
                        {
                            MainConsole.Instance.DebugFormat("[WebAPI]: API authentication failed for {0}", authorization["username"]);
                        }
                    }
                }
            }
            else
            {
                string opaque = UUID.Random().ToString();
                string nonce = UUID.Random().ToString();
                m_authNonces.Add(opaque, nonce, 5);
                response.StatusCode = 401;
                response.StatusDescription = "Unauthorized";
                string digestHeader = "Digest " + string.Join(", ", new string[]{
                        "realm=\"" + Name + "\"",
                        "qop=\"auth\"",
                        "nonce=\"" + nonce + "\"",
                        "opaque=\"" + opaque + "\""
                    });
                response.AddHeader("WWW-Authenticate", digestHeader);
            }
            return false;
        }

        public byte[] doAPICall(BaseStreamHandler caller, string path, Stream requestData, OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            OSDMap resp = new OSDMap();
            object checking = Enum.Parse(typeof(WebAPIHttpMethod), caller.HttpMethod);
            if (checking != null)
            {
                WebAPIHttpMethod HttpMethod = (WebAPIHttpMethod)checking;
                if (m_APIMethods.ContainsKey(HttpMethod))
                {
                    string methodPath = path.Substring(caller.Path.Length).Trim();
                    if (methodPath != string.Empty && methodPath.Substring(0, 1) == "/")
                    {
                        methodPath = methodPath.Substring(1);
                    }

                    string[] parts = new string[0];
                    if (methodPath != string.Empty)
                    {
                        parts = methodPath.Split('/');
                    }
                    for (int i = 0; i < parts.Length; ++i)
                    {
                        parts[i] = HttpUtility.UrlDecode(parts[i]);
                    }

                    if (parts.Length == 0)
                    {
                        httpResponse.StatusCode = 404;
                        return new byte[0];
                    }

                    string method = parts.Length < 1 ? string.Empty : parts[0];


                    try
                    {
                        OSDMap args = new OSDMap();
                        string body;
                        if (HttpMethod == WebAPIHttpMethod.GET)
                        {
                            foreach (string key in httpRequest.Query.Keys)
                            {
                                args[HttpUtility.UrlDecode(key)] = ((OSDMap)OSDParser.DeserializeJson("{\"foo\":" + HttpUtility.UrlDecode(httpRequest.Query[key].ToString()) + "}"))["foo"];
                            }
                            body = OSDParser.SerializeJsonString(args);
                            MainConsole.Instance.TraceFormat("[WebAPI]: HTTP GET {0} query String: {1}", method, body);
                        }
                        else if (HttpMethod == WebAPIHttpMethod.POST)
                        {
                            StreamReader sr = new StreamReader(requestData);
                            body = sr.ReadToEnd();
                            sr.Close();
                            body = body.Trim();
                            args = body == string.Empty ? new OSDMap(0) : (OSDMap)OSDParser.DeserializeJson(body);
                            MainConsole.Instance.TraceFormat("[WebAPI]: HTTP POST {0} query String: {1}", method, body);
                        }
                        //Make sure that the person who is calling can access the web service
                        if (AllowAPICall(method, httpRequest, httpResponse))
                        {
                            if (m_APIMethods[HttpMethod].ContainsKey(method))
                            {
                                object[] methodArgs = new object[1] { args }; ;
                                WebAPIMethod attr = (WebAPIMethod)Attribute.GetCustomAttribute(m_APIMethods[HttpMethod][method], typeof(WebAPIMethod));
                                if (attr.PassOnRequestingAgentID)
                                {
                                    methodArgs = new object[2] { args, authUser(httpRequest) };
                                }
                                resp = (OSDMap)m_APIMethods[HttpMethod][method].Invoke(this, methodArgs);
                            }
                            else
                            {
                                resp["Failed"] = "Unsupported method";
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        MainConsole.Instance.TraceFormat("[WebAPI] Exception thrown: " + e.ToString());
                    }
                    if (resp.Count == 0)
                    {
                        resp.Add("response", OSD.FromString("Failed"));
                    }
                }
                else
                {
                    httpResponse.StatusCode = 405;
                    resp["Failed"] = string.Format("No methods implemented for HTTP {0}", HttpMethod.ToString());
                }
            }
            else
            {
                httpResponse.StatusCode = 405;
                resp["Failed"] = "Method Not Allowed";
            }
            UTF8Encoding encoding = new UTF8Encoding();
            httpResponse.ContentType = "application/json";
            return encoding.GetBytes(OSDParser.SerializeJsonString(resp, true));
        }

        #region textures

        public Hashtable OnHTTPGetTextureImage(Hashtable keysvals)
        {
            Hashtable reply = new Hashtable();

            if (keysvals["method"].ToString() != "GridTexture")
                return reply;

            MainConsole.Instance.Debug("[WeAPI]: Sending image jpeg");
            int statuscode = 200;
            byte[] jpeg = new byte[0];
            IAssetService m_AssetService = m_registry.RequestModuleInterface<IAssetService>();

            MemoryStream imgstream = new MemoryStream();
            Bitmap mapTexture = new Bitmap(1, 1);
            ManagedImage managedImage;
            Image image = (Image)mapTexture;

            try
            {
                // Taking our jpeg2000 data, decoding it, then saving it to a byte array with regular jpeg data

                imgstream = new MemoryStream();

                // non-async because we know we have the asset immediately.
                AssetBase mapasset = m_AssetService.Get(keysvals["uuid"].ToString());

                // Decode image to System.Drawing.Image
                if (OpenJPEG.DecodeToImage(mapasset.Data, out managedImage, out image))
                {
                    // Save to bitmap

                    mapTexture = ResizeBitmap(image, 128, 128);
                    EncoderParameters myEncoderParameters = new EncoderParameters();
                    myEncoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);

                    // Save bitmap to stream
                    mapTexture.Save(imgstream, GetEncoderInfo("image/jpeg"), myEncoderParameters);



                    // Write the stream to a byte array for output
                    jpeg = imgstream.ToArray();
                }
            }
            catch (Exception)
            {
                // Dummy!
                MainConsole.Instance.Warn("[WebAPI]: Unable to post image.");
            }
            finally
            {
                // Reclaim memory, these are unmanaged resources
                // If we encountered an exception, one or more of these will be null
                if (mapTexture != null)
                    mapTexture.Dispose();

                if (image != null)
                    image.Dispose();

                if (imgstream != null)
                {
                    imgstream.Close();
                    imgstream.Dispose();
                }
            }


            reply["str_response_string"] = Convert.ToBase64String(jpeg);
            reply["int_response_code"] = statuscode;
            reply["content_type"] = "image/jpeg";

            return reply;
        }

        private Bitmap ResizeBitmap(Image b, int nWidth, int nHeight)
        {
            Bitmap newsize = new Bitmap(nWidth, nHeight);
            Graphics temp = Graphics.FromImage(newsize);
            temp.DrawImage(b, 0, 0, nWidth, nHeight);
            temp.SmoothingMode = SmoothingMode.AntiAlias;
            temp.DrawString(m_servernick, new Font("Arial", 8, FontStyle.Regular), new SolidBrush(Color.FromArgb(90, 255, 255, 50)), new Point(2, 115));

            return newsize;
        }

        // From msdn
        private static ImageCodecInfo GetEncoderInfo(String mimeType)
        {
            ImageCodecInfo[] encoders;
            encoders = ImageCodecInfo.GetImageEncoders();
            for (int j = 0; j < encoders.Length; ++j)
            {
                if (encoders[j].MimeType == mimeType)
                    return encoders[j];
            }
            return null;
        }

        #endregion

        #endregion

        #region WebAPI methods

        #region Grid

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap OnlineStatus(OSDMap map)
        {
            ILoginService loginService = m_registry.RequestModuleInterface<ILoginService>();
            bool LoginEnabled = loginService.MinLoginLevel == 0;

            OSDMap resp = new OSDMap();
            resp["Online"] = OSD.FromBoolean(true);
            resp["LoginEnabled"] = OSD.FromBoolean(LoginEnabled);

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap get_grid_info(OSDMap map)
        {
            OSDMap resp = new OSDMap();
            resp["GridInfo"] = m_GridInfo;
            return resp;
        }

        #endregion

        #region Account

        /// <summary>
        /// Changes user name
        /// </summary>
        /// <param name="map">UUID, FirstName, LastName</param>
        /// <returns>Verified</returns>
        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap ChangeName(OSDMap map)
        {
            IUserAccountService accountService = m_registry.RequestModuleInterface<IUserAccountService>();
            UserAccount user = accountService.GetUserAccount(UUID.Zero, map["UUID"].AsUUID());
            OSDMap resp = new OSDMap();

            bool verified = user != null;
            resp["Verified"] = OSD.FromBoolean(verified);
            if (verified)
            {
                user.Name = map["Name"].AsString();
                resp["Stored" ] = OSD.FromBoolean(accountService.StoreUserAccount(user));
                accountService.CacheAccount(user);
            }

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap EditUser(OSDMap map)
        {
            bool editRLInfo = (map.ContainsKey("RLName") && map.ContainsKey("RLAddress") && map.ContainsKey("RLZip") && map.ContainsKey("RLCity") && map.ContainsKey("RLCountry"));
            OSDMap resp = new OSDMap();
            resp["agent"] = OSD.FromBoolean(!editRLInfo); // if we have no RLInfo, editing account is assumed to be successful.
            resp["account"] = OSD.FromBoolean(false);
            UUID principalID = map["UserID"].AsUUID();
            UserAccount account = m_registry.RequestModuleInterface<IUserAccountService>().GetUserAccount(UUID.Zero, principalID);
            if(account != null)
            {
                account.Email = map["Email"];
                if (m_registry.RequestModuleInterface<IUserAccountService>().GetUserAccount(UUID.Zero, map["Name"].AsString()) == null)
                {
                    account.Name = map["Name"];
                }
                if (map.ContainsKey("UserLevel"))
                {
                    account.UserLevel = map["UserLevel"].AsInteger();
                }

                if (editRLInfo)
                {
                    IAgentConnector agentConnector = Aurora.DataManager.DataManager.RequestPlugin<IAgentConnector>();
                    IAgentInfo agent = agentConnector.GetAgent(account.PrincipalID);
                    if (agent == null)
                    {
                        agentConnector.CreateNewAgent(account.PrincipalID);
                        agent = agentConnector.GetAgent(account.PrincipalID);
                    }
                    if (agent != null)
                    {
                        agent.OtherAgentInformation["RLName"] = map["RLName"];
                        agent.OtherAgentInformation["RLAddress"] = map["RLAddress"];
                        agent.OtherAgentInformation["RLZip"] = map["RLZip"];
                        agent.OtherAgentInformation["RLCity"] = map["RLCity"];
                        agent.OtherAgentInformation["RLCountry"] = map["RLCountry"];
                        agentConnector.UpdateAgent(agent);
                        resp["agent"] = OSD.FromBoolean(true);
                    }
                }
                resp["account"] = OSD.FromBoolean(m_registry.RequestModuleInterface<IUserAccountService>().StoreUserAccount(account));
            }
            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap ResetAvatar(OSDMap map)
        {
            OSDMap resp = new OSDMap();
            UUID user = UUID.Zero;

            if (!map.ContainsKey("User"))
            {
                resp["Failed"] = new OSDString("User not specified.");
            }
            else if (!UUID.TryParse(map["User"].AsString(), out user))
            {
                resp["Failed"] = new OSDString("User specified but was not valid UUID.");
            }
            else
            {
                IAvatarService avatarService = m_registry.RequestModuleInterface<IAvatarService>();

                if (avatarService == null)
                {
                    resp["Failed"] = new OSDString("Avatar service could not be fetched.");
                }
                else
                {
                    resp["Success"] = new OSDBoolean(avatarService.ResetAvatar(user));
                }
            }


            return resp;
        }

        #region Registration

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap CheckIfUserExists(OSDMap map)
        {
            IUserAccountService accountService = m_registry.RequestModuleInterface<IUserAccountService>();
            UserAccount user = accountService.GetUserAccount(UUID.Zero, map["Name"].AsString());

            bool Verified = user != null;
            OSDMap resp = new OSDMap();
            resp["Verified"] = OSD.FromBoolean(Verified);
            resp["UUID"] = OSD.FromUUID(Verified ? user.PrincipalID : UUID.Zero);
            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap GetAvatarArchives(OSDMap map)
        {
            OSDMap resp = new OSDMap();

            List<AvatarArchive> temp = Aurora.DataManager.DataManager.RequestPlugin<IAvatarArchiverConnector>().GetAvatarArchives(true);

            OSDArray names = new OSDArray();
            OSDArray snapshot = new OSDArray();

            MainConsole.Instance.DebugFormat("[WebAPI]: {0} avatar archives found", temp.Count);

            foreach (AvatarArchive a in temp)
            {
                names.Add(OSD.FromString(a.Name));
                snapshot.Add(OSD.FromUUID(UUID.Parse(a.Snapshot)));
            }

            resp["names"] = names;
            resp["snapshot"] = snapshot;

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap CreateAccount(OSDMap map)
        {
            bool Verified = false;
            string Name = map["Name"].AsString();
            string PasswordHash = map["PasswordHash"].AsString();
            //string PasswordSalt = map["PasswordSalt"].AsString();
            string HomeRegion = map["HomeRegion"].AsString();
            string Email = map["Email"].AsString();
            string AvatarArchive = map["AvatarArchive"].AsString();
            int userLevel = map["UserLevel"].AsInteger();

            bool activationRequired = map.ContainsKey("ActivationRequired") ? map["ActivationRequired"].AsBoolean() : false;
  

            IUserAccountService accountService = m_registry.RequestModuleInterface<IUserAccountService>();
            if (accountService == null)
                return null;

            PasswordHash = PasswordHash.StartsWith("$1$") ? PasswordHash.Remove(0, 3) : Util.Md5Hash(PasswordHash); //remove $1$

            accountService.CreateUser(Name, PasswordHash, Email);
            UserAccount user = accountService.GetUserAccount(UUID.Zero, Name);
            IAgentInfoService agentInfoService = m_registry.RequestModuleInterface<IAgentInfoService> ();
            IGridService gridService = m_registry.RequestModuleInterface<IGridService> ();
            if (agentInfoService != null && gridService != null)
            {
                UUID homeRegion;
                GridRegion r = UUID.TryParse(HomeRegion, out homeRegion) ? gridService.GetRegionByUUID(UUID.Zero, homeRegion) : gridService.GetRegionByName (UUID.Zero, HomeRegion);
                if (r != null)
                {
                    agentInfoService.SetHomePosition(user.PrincipalID.ToString(), r.RegionID, new Vector3(r.RegionSizeX / 2, r.RegionSizeY / 2, 20), Vector3.Zero);
                }
                else
                {
                    MainConsole.Instance.DebugFormat("[WebAPI]: Could not set home position for user {0}, region \"{1}\" did not produce a result from the grid service", user.PrincipalID.ToString(), HomeRegion);
                }
            }

            Verified = user != null;
            UUID userID = UUID.Zero;

            OSDMap resp = new OSDMap();
            resp["Verified"] = OSD.FromBoolean(Verified);

            if (Verified)
            {
                userID = user.PrincipalID;
                user.UserLevel = userLevel;

                // could not find a way to save this data here.
                DateTime RLDOB = map["RLDOB"].AsDate();
                string RLFirstName = map["RLFirstName"].AsString();
                string RLLastName = map["RLLastName"].AsString();
                string RLAddress = map["RLAddress"].AsString();
                string RLCity = map["RLCity"].AsString();
                string RLZip = map["RLZip"].AsString();
                string RLCountry = map["RLCountry"].AsString();
                string RLIP = map["RLIP"].AsString();

                IAgentConnector con = Aurora.DataManager.DataManager.RequestPlugin<IAgentConnector> ();
                con.CreateNewAgent (userID);

                IAgentInfo agent = con.GetAgent (userID);
                agent.OtherAgentInformation["RLDOB"] = RLDOB;
                agent.OtherAgentInformation["RLFirstName"] = RLFirstName;
                agent.OtherAgentInformation["RLLastName"] = RLLastName;
                agent.OtherAgentInformation["RLAddress"] = RLAddress;
                agent.OtherAgentInformation["RLCity"] = RLCity;
                agent.OtherAgentInformation["RLZip"] = RLZip;
                agent.OtherAgentInformation["RLCountry"] = RLCountry;
                agent.OtherAgentInformation["RLIP"] = RLIP;
                if (activationRequired)
                {
                    UUID activationToken = UUID.Random();
                    agent.OtherAgentInformation["WebUIActivationToken"] = Util.Md5Hash(activationToken.ToString() + ":" + PasswordHash);
                    resp["WebUIActivationToken"] = activationToken;
                }
                con.UpdateAgent (agent);
                
                accountService.StoreUserAccount(user);

                IProfileConnector profileData = Aurora.DataManager.DataManager.RequestPlugin<IProfileConnector>();
                IUserProfileInfo profile = profileData.GetUserProfile(user.PrincipalID);
                if (profile == null)
                {
                    profileData.CreateNewProfile(user.PrincipalID);
                    profile = profileData.GetUserProfile(user.PrincipalID);
                }
                if (AvatarArchive.Length > 0)
                    profile.AArchiveName = AvatarArchive + ".database";

                profile.IsNewUser = true;
                profileData.UpdateUserProfile(profile);
            }

            resp["UUID"] = OSD.FromUUID(userID);
            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap Authenticated(OSDMap map)
        {
            IUserAccountService accountService = m_registry.RequestModuleInterface<IUserAccountService>();
            UserAccount user = accountService.GetUserAccount(UUID.Zero, map["UUID"].AsUUID());

            bool Verified = user != null;
            OSDMap resp = new OSDMap();
            resp["Verified"] = OSD.FromBoolean(Verified);

            if (Verified)
            {
                user.UserLevel = map.ContainsKey("value") ? map["value"].AsInteger() : 0;
                accountService.StoreUserAccount(user);
                IAgentConnector con = Aurora.DataManager.DataManager.RequestPlugin<IAgentConnector>();
                IAgentInfo agent = con.GetAgent(user.PrincipalID);
                if (agent != null && agent.OtherAgentInformation.ContainsKey("WebUIActivationToken"))
                {
                    agent.OtherAgentInformation.Remove("WebUIActivationToken");
                    con.UpdateAgent(agent);
                }
            }

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap ActivateAccount(OSDMap map)
        {
            OSDMap resp = new OSDMap();
            resp["Verified"] = OSD.FromBoolean(false);

            if (map.ContainsKey("UserName") && map.ContainsKey("PasswordHash") && map.ContainsKey("ActivationToken"))
            {
                IUserAccountService accountService = m_registry.RequestModuleInterface<IUserAccountService>();
                UserAccount user = accountService.GetUserAccount(UUID.Zero, map["UserName"].ToString());
                if (user != null)
                {
                    IAgentConnector con = Aurora.DataManager.DataManager.RequestPlugin<IAgentConnector>();
                    IAgentInfo agent = con.GetAgent(user.PrincipalID);
                    if (agent != null && agent.OtherAgentInformation.ContainsKey("WebUIActivationToken"))
                    {
                        UUID activationToken = map["ActivationToken"];
                        string WebUIActivationToken = agent.OtherAgentInformation["WebUIActivationToken"];
                        string PasswordHash = map["PasswordHash"];
                        if (!PasswordHash.StartsWith("$1$"))
                        {
                            PasswordHash = "$1$" + Util.Md5Hash(PasswordHash);
                        }
                        PasswordHash = PasswordHash.Remove(0, 3); //remove $1$

                        bool verified = Utils.MD5String(activationToken.ToString() + ":" + PasswordHash) == WebUIActivationToken;
                        resp["Verified"] = verified;
                        if (verified)
                        {
                            user.UserLevel = 0;
                            accountService.StoreUserAccount(user);
                            agent.OtherAgentInformation.Remove("WebUIActivationToken");
                            con.UpdateAgent(agent);
                        }
                    }
                }
            }

            return resp;
        }

        #endregion

        #region Login
        private OSDMap doLogin(OSDMap map, bool asAdmin)
        {
            string Name = map["Name"].AsString();
            string Password = map["Password"].AsString();

            ILoginService loginService = m_registry.RequestModuleInterface<ILoginService>();
            IUserAccountService accountService = m_registry.RequestModuleInterface<IUserAccountService>();
            IAgentConnector agentConnector = Aurora.DataManager.DataManager.RequestPlugin<IAgentConnector>();
            UserAccount account = null;
            OSDMap resp = new OSDMap();
            resp["Verified"] = OSD.FromBoolean(false);


            account = accountService.GetUserAccount(UUID.Zero, Name);
            if (agentConnector == null || accountService == null)
            {
                return resp;
            }

            account = accountService.GetUserAccount(UUID.Zero, Name);

            if (account != null)
            {
                if (loginService.VerifyClient(account.PrincipalID, Name, "UserAccount", Password, account.ScopeID))
                {
                    account = accountService.GetUserAccount(UUID.Zero, Name);
                    if (asAdmin)
                    {
                        IAgentInfo agent = agentConnector.GetAgent(account.PrincipalID);
                        if (agent.OtherAgentInformation["WebUIEnabled"].AsBoolean() == false)
                        {
                            return resp;
                        }
                    }
                    resp["UUID"] = OSD.FromUUID(account.PrincipalID);
                    resp["FirstName"] = OSD.FromString(account.FirstName);
                    resp["LastName"] = OSD.FromString(account.LastName);
                    resp["Email"] = OSD.FromString(account.Email);
                    resp["Verified"] = OSD.FromBoolean(true);
                    MainConsole.Instance.Trace("Login for " + Name + " was successful");
                }
                else
                {
                    MainConsole.Instance.Trace("Login for " + Name + " was not successful");
                }
            }

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap Login(OSDMap map)
        {
            return doLogin(map, false);
        }

        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap AdminLogin(OSDMap map)
        {
            return doLogin(map, false);
        }

        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap SetWebLoginKey(OSDMap map)
        {
            OSDMap resp = new OSDMap ();
            UUID principalID = map["PrincipalID"].AsUUID();
            UUID webLoginKey = UUID.Random();
            IAuthenticationService authService = m_registry.RequestModuleInterface<IAuthenticationService>();
            IAuthenticationData authData = Aurora.DataManager.DataManager.RequestPlugin<IAuthenticationData>();
            if (authService != null && authData != null)
            {
                //Remove the old
                authData.Delete(principalID, "WebLoginKey");
                authService.SetPlainPassword(principalID, "WebLoginKey", webLoginKey.ToString());
                resp["WebLoginKey"] = webLoginKey;
            }
            resp["Failed"] = OSD.FromString(String.Format("No auth service, cannot set WebLoginKey for user {0}.", map["PrincipalID"].AsUUID().ToString()));

            return resp;
        }

        #endregion

        #region Email

        /// <summary>
        /// After conformation the email is saved
        /// </summary>
        /// <param name="map">UUID, Email</param>
        /// <returns>Verified</returns>
        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap SaveEmail(OSDMap map)
        {
            string email = map["Email"].AsString();

            IUserAccountService accountService = m_registry.RequestModuleInterface<IUserAccountService>();
            UserAccount user = accountService.GetUserAccount(UUID.Zero, map["UUID"].AsUUID());
            OSDMap resp = new OSDMap();

            bool verified = user != null;
            resp["Verified"] = OSD.FromBoolean(verified);
            if (verified)
            {
                user.Email = email;
                user.UserLevel = 0;
                accountService.StoreUserAccount(user);
            }
            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap ConfirmUserEmailName(OSDMap map)
        {
            string Name = map["Name"].AsString();
            string Email = map["Email"].AsString();

            OSDMap resp = new OSDMap();
            IUserAccountService accountService = m_registry.RequestModuleInterface<IUserAccountService>();
            UserAccount user = accountService.GetUserAccount(UUID.Zero, Name);
            bool verified = user != null;
            resp["Verified"] = OSD.FromBoolean(verified);

            if (verified)
            {
                resp["UUID"] = OSD.FromUUID(user.PrincipalID);
                if (user.UserLevel >= 0)
                {
                    if (user.Email.ToLower() != Email.ToLower())
                    {
                        MainConsole.Instance.TraceFormat("User email for account \"{0}\" is \"{1}\" but \"{2}\" was specified.", Name, user.Email.ToString(), Email);
                        resp["Error"] = OSD.FromString("Email does not match the user name.");
                        resp["ErrorCode"] = OSD.FromInteger(3);
                    }
                }
                else
                {
                    resp["Error"] = OSD.FromString("This account is disabled.");
                    resp["ErrorCode"] = OSD.FromInteger(2);
                }
            }
            else
            {
                resp["Error"] = OSD.FromString("No such user.");
                resp["ErrorCode"] = OSD.FromInteger(1);
            }


            return resp;
        }

        #endregion

        #region password

        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap ChangePassword(OSDMap map)
        {
            OSDMap resp = new OSDMap();

            string Password = map["Password"].AsString();
            string newPassword = map["NewPassword"].AsString();
            UUID userID = map["UUID"].AsUUID();

            ILoginService loginService = m_registry.RequestModuleInterface<ILoginService>();
            IUserAccountService accountService = m_registry.RequestModuleInterface<IUserAccountService>();
            IAuthenticationService auths = m_registry.RequestModuleInterface<IAuthenticationService>();

            UserAccount account = accountService.GetUserAccount(UUID.Zero, userID);

            //Null means it went through without an error
            bool Verified = loginService.VerifyClient(account.PrincipalID, account.Name, "UserAccount", Password, account.ScopeID);

            if ((auths.Authenticate(userID, "UserAccount", Password.StartsWith("$1$") ? Password.Remove(0, 3) : Util.Md5Hash(Password), 100) != string.Empty) && (Verified))
            {
                auths.SetPasswordHashed(userID, "UserAccount", newPassword.StartsWith("$1$") ? newPassword.Remove(0, 3) : Util.Md5Hash(newPassword));
                resp["Verified"] = OSD.FromBoolean(Verified);
            }

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap ForgotPassword(OSDMap map)
        {
            UUID UUDI = map["UUID"].AsUUID();
            string Password = map["Password"].AsString();

            IUserAccountService accountService = m_registry.RequestModuleInterface<IUserAccountService>();
            UserAccount user = accountService.GetUserAccount(UUID.Zero, UUDI);

            OSDMap resp = new OSDMap();
            bool verified = user != null;
            resp["Verified"] = OSD.FromBoolean(verified);
            resp["UserLevel"] = OSD.FromInteger(0);
            if (verified)
            {
                resp["UserLevel"] = OSD.FromInteger(user.UserLevel);
                if (user.UserLevel >= 0)
                {
                    IAuthenticationService auths = m_registry.RequestModuleInterface<IAuthenticationService>();
                    auths.SetPassword (user.PrincipalID, "UserAccount", Password);
                }
                else
                {
                    resp["Verified"] = OSD.FromBoolean(false);
                }
            }

            return resp;
        }

        #endregion

        #endregion

        #region Users

        private OSDMap UserAccount2InfoWebOSD(UserAccount user)
        {
            OSDMap resp = new OSDMap();

            IAgentInfoService agentService = m_registry.RequestModuleInterface<IAgentInfoService>();

            UserInfo userinfo = agentService.GetUserInfo(user.PrincipalID.ToString());
            IGridService gs = m_registry.RequestModuleInterface<IGridService>();
            GridRegion homeRegion = null;
            GridRegion currentRegion = null;
            if (userinfo != null)
            {
                homeRegion = gs.GetRegionByUUID(UUID.Zero, userinfo.HomeRegionID);
                currentRegion = userinfo.CurrentRegionID != UUID.Zero ? gs.GetRegionByUUID(UUID.Zero, userinfo.CurrentRegionID) : null;
            }

            resp["UUID"] = OSD.FromUUID(user.PrincipalID);
            resp["HomeUUID"] = OSD.FromUUID((homeRegion == null) ? UUID.Zero : homeRegion.RegionID);
            resp["HomeName"] = OSD.FromString((homeRegion == null) ? "" : homeRegion.RegionName);
            resp["CurrentRegionUUID"] = OSD.FromUUID((currentRegion == null) ? UUID.Zero : currentRegion.RegionID);
            resp["CurrentRegionName"] = OSD.FromString((currentRegion == null) ? "" : currentRegion.RegionName);
            resp["Online"] = OSD.FromBoolean((userinfo == null) ? false : userinfo.IsOnline);
            resp["Email"] = OSD.FromString(user.Email);
            resp["Name"] = OSD.FromString(user.Name);
            resp["FirstName"] = OSD.FromString(user.FirstName);
            resp["LastName"] = OSD.FromString(user.LastName);
            resp["LastLogin"] = userinfo == null ? OSD.FromBoolean(false) : OSD.FromInteger((int)Utils.DateTimeToUnixTime(userinfo.LastLogin));
            resp["LastLogout"] = userinfo == null ? OSD.FromBoolean(false) : OSD.FromInteger((int)Utils.DateTimeToUnixTime(userinfo.LastLogout));

            return resp;
        }

        private OSDMap UserInfo2InfoWebOSD(UserInfo userinfo)
        {
            OSDMap resp = new OSDMap();

            IUserAccountService accountService = m_registry.RequestModuleInterface<IUserAccountService>();
            IGridService gs = m_registry.RequestModuleInterface<IGridService>();
            UserAccount user = accountService.GetUserAccount(UUID.Zero, new UUID(userinfo.UserID));

            GridRegion homeRegion = gs.GetRegionByUUID(UUID.Zero, userinfo.HomeRegionID);
            GridRegion currentRegion = userinfo.CurrentRegionID != UUID.Zero ? gs.GetRegionByUUID(UUID.Zero, userinfo.CurrentRegionID) : null;

            resp["UUID"] = OSD.FromUUID(user.PrincipalID);
            resp["HomeUUID"] = OSD.FromUUID((homeRegion == null) ? UUID.Zero : homeRegion.RegionID);
            resp["HomeName"] = OSD.FromString((homeRegion == null) ? "" : homeRegion.RegionName);
            resp["CurrentRegionUUID"] = OSD.FromUUID((userinfo == null) ? UUID.Zero : userinfo.CurrentRegionID);
            resp["CurrentRegionName"] = OSD.FromString((currentRegion == null) ? "" : currentRegion.RegionName);
            resp["Online"] = OSD.FromBoolean((userinfo == null) ? false : userinfo.IsOnline);
            resp["Email"] = OSD.FromString(user.Email);
            resp["Name"] = OSD.FromString(user.Name);
            resp["FirstName"] = OSD.FromString(user.FirstName);
            resp["LastName"] = OSD.FromString(user.LastName);
            resp["LastLogin"] = userinfo == null ? OSD.FromBoolean(false) : OSD.FromInteger((int)Utils.DateTimeToUnixTime(userinfo.LastLogin));
            resp["LastLogout"] = userinfo == null ? OSD.FromBoolean(false) : OSD.FromInteger((int)Utils.DateTimeToUnixTime(userinfo.LastLogout));

            return resp;
        }

        /// <summary>
        /// Gets user information for change user info page on site
        /// </summary>
        /// <param name="map">UUID</param>
        /// <returns>Verified, HomeName, HomeUUID, Online, Email, FirstName, LastName</returns>
        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap GetGridUserInfo(OSDMap map)
        {
            string uuid = String.Empty;
            uuid = map["UUID"].AsString();

            IUserAccountService accountService = m_registry.RequestModuleInterface<IUserAccountService>();
            UserAccount user = accountService.GetUserAccount(UUID.Zero, map["UUID"].AsUUID());
            IAgentInfoService agentService = m_registry.RequestModuleInterface<IAgentInfoService>();

            OSDMap resp = new OSDMap();
            bool verified = user != null;
            resp["Verified"] = OSD.FromBoolean(verified);
            if (verified)
            {
                resp = UserAccount2InfoWebOSD(user);
            }

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap GetProfile(OSDMap map)
        {
            OSDMap resp = new OSDMap();
            string Name = map["Name"].AsString();
            UUID userID = map["UUID"].AsUUID();

            IUserAccountService accountService = m_registry.RequestModuleInterface<IUserAccountService>();

            if (accountService == null)
            {
                resp["Failed"] = new OSDString("Could not find IUserAccountService");
                return resp;
            }

            UserAccount account = Name != "" ? accountService.GetUserAccount(UUID.Zero, Name) : accountService.GetUserAccount(UUID.Zero, userID);
            if (account != null)
            {
                OSDMap accountMap = new OSDMap();

                accountMap["Created"] = account.Created;
                accountMap["Name"] = account.Name;
                accountMap["PrincipalID"] = account.PrincipalID;
                accountMap["Email"] = account.Email;
                accountMap["UserLevel"] = account.UserLevel;
                accountMap["UserFlags"] = account.UserFlags;

                TimeSpan diff = DateTime.Now - Util.ToDateTime(account.Created);
                int years = (int)diff.TotalDays / 356;
                int days = years > 0 ? (int)diff.TotalDays / years : (int)diff.TotalDays;
                accountMap["TimeSinceCreated"] = years + " years, " + days + " days"; // if we're sending account.Created do we really need to send this string ?

                IProfileConnector profileConnector = Aurora.DataManager.DataManager.RequestPlugin<IProfileConnector>();
                IUserProfileInfo profile = profileConnector.GetUserProfile(account.PrincipalID);
                if (profile != null)
                {
                    resp["profile"] = profile.ToOSD(false);//not trusted, use false

                    if (account.UserFlags == 0)
                    {
                        account.UserFlags = 2; //Set them to no info given
                    }

                    string flags = ((IUserProfileInfo.ProfileFlags)account.UserFlags).ToString();
                    IUserProfileInfo.ProfileFlags.NoPaymentInfoOnFile.ToString();

                    accountMap["AccountInfo"] = (profile.CustomType != "" ? profile.CustomType : account.UserFlags == 0 ? "Resident" : "Admin") + "\n" + flags;
                    UserAccount partnerAccount = m_registry.RequestModuleInterface<IUserAccountService>().GetUserAccount(UUID.Zero, profile.Partner);
                    if (partnerAccount != null)
                    {
                        accountMap["Partner"] = partnerAccount.Name;
                        accountMap["PartnerUUID"] = partnerAccount.PrincipalID;
                    }
                    else
                    {
                        accountMap["Partner"] = "";
                        accountMap["PartnerUUID"] = UUID.Zero;
                    }

                }
                IAgentConnector agentConnector = Aurora.DataManager.DataManager.RequestPlugin<IAgentConnector>();
                IAgentInfo agent = agentConnector.GetAgent(account.PrincipalID);
                if (agent != null)
                {
                    OSDMap agentMap = new OSDMap();
                    agentMap["Flags"] = (int)agent.Flags;
                    agentMap["RLName"] = agent.OtherAgentInformation["RLName"].AsString();
                    agentMap["RLAddress"] = agent.OtherAgentInformation["RLAddress"].AsString();
                    agentMap["RLZip"] = agent.OtherAgentInformation["RLZip"].AsString();
                    agentMap["RLCity"] = agent.OtherAgentInformation["RLCity"].AsString();
                    agentMap["RLCountry"] = agent.OtherAgentInformation["RLCountry"].AsString();
                    resp["agent"] = agentMap;
                }
                resp["account"] = accountMap;
            }

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap FindUsers(OSDMap map)
        {
            OSDMap resp = new OSDMap();
            IUserAccountService accountService = m_registry.RequestModuleInterface<IUserAccountService>();
            IAgentConnector agentConnector = Aurora.DataManager.DataManager.RequestPlugin<IAgentConnector>();
            if (accountService == null)
            {
                resp["Failed"] = new OSDString("Could not find IUserAccountService");
            }
            else if (agentConnector == null)
            {
                resp["Failed"] = new OSDString("Could not find IAgentConnector");
            }
            else
            {
                UUID scopeID = map.ContainsKey("ScopeID") ? map["ScopeID"].AsUUID() : UUID.Zero;
                uint start = map.ContainsKey("Start") ? map["Start"].AsUInteger() : 0;
                uint count = map.ContainsKey("Count") ? map["Count"].AsUInteger() : 10;
                string Query = map["Query"].AsString();
                List<UserAccount> accounts = accountService.GetUserAccounts(scopeID, Query, start, count);

                OSDArray users = new OSDArray();
                MainConsole.Instance.TraceFormat("{0} accounts found", accounts.Count);
                foreach (UserAccount acc in accounts)
                {
                    OSDMap userInfo = new OSDMap();
                    userInfo["PrincipalID"] = acc.PrincipalID;
                    userInfo["UserName"] = acc.Name;
                    userInfo["Created"] = acc.Created;
                    userInfo["UserFlags"] = acc.UserFlags;
                    userInfo["UserLevel"] = acc.UserLevel;
                    IAgentInfo agent = agentConnector.GetAgent(acc.PrincipalID);
                    if (agent == null)
                    {
                        MainConsole.Instance.ErrorFormat("Could not get IAgentInfo for {0} ({1})", acc.Name, acc.PrincipalID);
                    }
                    userInfo["Flags"] = (agent == null) ? 0 : (int)agent.Flags;
                    users.Add(userInfo);
                }
                resp["Users"] = users;

                resp["Start"] = OSD.FromInteger(start);
                resp["Count"] = OSD.FromInteger(count);
                resp["Query"] = OSD.FromString(Query);
                resp["Total"] = OSD.FromInteger((int)accountService.NumberOfUserAccounts(scopeID, Query));
            }

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap GetFriends(OSDMap map)
        {
            OSDMap resp = new OSDMap();

            if (map.ContainsKey("UserID") == false)
            {
                resp["Failed"] = OSD.FromString("User ID not specified.");
                return resp;
            }

            IFriendsService friendService = m_registry.RequestModuleInterface<IFriendsService>();

            if (friendService == null)
            {
                resp["Failed"] = OSD.FromString("No friend service found.");
                return resp;
            }

            List<FriendInfo> friendsList = new List<FriendInfo>(friendService.GetFriends(map["UserID"].AsUUID()));
            OSDArray friends = new OSDArray(friendsList.Count);
            foreach (FriendInfo friendInfo in friendsList)
            {
                UserAccount account = m_registry.RequestModuleInterface<IUserAccountService>().GetUserAccount(UUID.Zero, UUID.Parse(friendInfo.Friend));
                OSDMap friend = new OSDMap(4);
                friend["PrincipalID"] = friendInfo.Friend;
                friend["Name"] = account.Name;
                friend["MyFlags"] = friendInfo.MyFlags;
                friend["TheirFlags"] = friendInfo.TheirFlags;
                friends.Add(friend);
            }

            resp["Friends"] = friends;

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap DeleteUser(OSDMap map)
        {
            OSDMap resp = new OSDMap();
            resp["Finished"] = OSD.FromBoolean(true);

            UUID agentID = map["UserID"].AsUUID();
            IAgentInfo GetAgent = Aurora.DataManager.DataManager.RequestPlugin<IAgentConnector>().GetAgent(agentID);

            if (GetAgent != null)
            {
                GetAgent.Flags |= IAgentFlags.PermBan;
                Aurora.DataManager.DataManager.RequestPlugin<IAgentConnector>().UpdateAgent(GetAgent);
            }
            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap SetHomeLocation(OSDMap map)
        {
            OSDMap resp = new OSDMap();
            IAgentInfoService agentService = m_registry.RequestModuleInterface<IAgentInfoService>();
            IGridService gridService = m_registry.RequestModuleInterface<IGridService>();
            UserInfo userinfo = (map.ContainsKey("User") && agentService != null) ? agentService.GetUserInfo(map["User"].AsString()) : null;

            if (!map.ContainsKey("User"))
            {
                resp["Failed"] = new OSDString("No user specified");
            }
            else if (!map.ContainsKey("RegionID") && !map.ContainsKey("Position") && !map.ContainsKey("LookAt"))
            {
                resp["Failed"] = new OSDString("No position info specified");
            }
            else if (agentService == null)
            {
                resp["Failed"] = new OSDString("Could not get IAgentInfoService");
            }
            else if (gridService == null)
            {
                resp["Failed"] = new OSDString("Could not get IGridService");
            }
            else if (userinfo == null)
            {
                resp["Failed"] = new OSDString("Could not find user");
            }
            else
            {
                UUID scopeID = UUID.Zero;
                UUID regionID = UUID.Zero;
                Vector3 position = Vector3.Zero;
                Vector3 lookAt = Vector3.Zero;

                List<string> fail = new List<string>();

                if (map.ContainsKey("ScopeID") && !UUID.TryParse(map["ScopeID"].AsString(), out scopeID))
                {
                    fail.Add("ScopeID was specified but was not a valid UUID");
                }
                if (map.ContainsKey("RegionID") && !UUID.TryParse(map["RegionID"].AsString(), out regionID))
                {
                    fail.Add("RegionID was specified but was not valid UUID");
                }
                if (map.ContainsKey("Position") && !Vector3.TryParse(map["Position"].AsString(), out position))
                {
                    fail.Add("Position was specified but was not valid Vector3");
                }
                if (map.ContainsKey("LookAt") && !Vector3.TryParse(map["LookAt"].AsString(), out lookAt))
                {
                    fail.Add("LookAt was specified but was not valid Vector3");
                }

                if (regionID == UUID.Zero)
                {
                    regionID = userinfo.HomeRegionID;
                }
                if (gridService.GetRegionByUUID(UUID.Zero, regionID) == null)
                {
                    fail.Add("region does not exist");
                }

                if (regionID == UUID.Zero && (map.ContainsKey("Position") || map.ContainsKey("LookAt")))
                {
                    fail.Add("Cannot change home location without specifying a region");
                }

                if (fail.Count > 0)
                {
                    resp["Failed"] = new OSDString(string.Join(". ", fail.ToArray()));
                    return resp;
                }

                userinfo.HomeRegionID = regionID;
                if (map.ContainsKey("Position"))
                {
                    userinfo.HomePosition = position;
                }
                if (map.ContainsKey("LookAt"))
                {
                    userinfo.HomeLookAt = lookAt;
                }

                resp["Success"] = new OSDBoolean(agentService.SetHomePosition(userinfo.UserID, userinfo.HomeRegionID, userinfo.HomePosition, userinfo.HomeLookAt));
            }

            return resp;
        }

        #region statistics

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap NumberOfRecentlyOnlineUsers(OSDMap map)
        {
            uint secondsAgo = map.ContainsKey("secondsAgo") ? uint.Parse(map["secondsAgo"]) : 0;
            bool stillOnline = map.ContainsKey("stillOnline") ? uint.Parse(map["stillOnline"]) == 1 : false;
            IAgentInfoConnector users = DataManager.DataManager.RequestPlugin<IAgentInfoConnector>();

            OSDMap resp = new OSDMap();
            resp["secondsAgo"] = OSD.FromInteger((int)secondsAgo);
            resp["stillOnline"] = OSD.FromBoolean(stillOnline);
            resp["result"] = OSD.FromInteger(users != null ? (int)users.RecentlyOnline(secondsAgo, stillOnline) : 0);

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap RecentlyOnlineUsers(OSDMap map)
        {
            OSDMap resp = new OSDMap();

            uint secondsAgo = map.ContainsKey("secondsAgo") ? uint.Parse(map["secondsAgo"]) : 0;
            bool stillOnline = map.ContainsKey("stillOnline") ? uint.Parse(map["stillOnline"]) == 1 : false;
            uint start = map.ContainsKey("Start") ? map["Start"].AsUInteger() : 0;
            uint count = map.ContainsKey("Count") ? map["Count"].AsUInteger() : 10;

            IAgentInfoConnector userInfoService = DataManager.DataManager.RequestPlugin<IAgentInfoConnector>();
            if (userInfoService == null)
            {
                resp["Failed"] = new OSDString("Could not get IAgentInfoConnector");
            }
            else
            {
                resp["Start"] = OSD.FromInteger((int)start);
                resp["Count"] = OSD.FromInteger((int)count);
                resp["Total"] = OSD.FromInteger((int)userInfoService.RecentlyOnline(secondsAgo, stillOnline));

                OSDArray Users = new OSDArray();
                Dictionary<string, bool> sort = new Dictionary<string, bool>(1);
                sort["LastSeen"] = true;
                List<UserInfo> users = userInfoService.RecentlyOnline(secondsAgo, stillOnline, sort, start, count);

                foreach (UserInfo userinfo in users)
                {
                    Users.Add(UserInfo2InfoWebOSD(userinfo));
                }

                resp["Users"] = Users;
            }

            return resp;
        }

        #endregion

        #region banning

        private void doBan(UUID agentID, DateTime? until){
            IAgentConnector agentConnector = Aurora.DataManager.DataManager.RequestPlugin<IAgentConnector>();
            IAgentInfo GetAgent = agentConnector.GetAgent(agentID);
            if (GetAgent != null)
            {
                GetAgent.Flags |= (until.HasValue) ? IAgentFlags.TempBan : IAgentFlags.PermBan;
                if (until.HasValue)
                {
                    GetAgent.OtherAgentInformation["TemperaryBanInfo"] = until.Value.ToString("s");
                    MainConsole.Instance.TraceFormat("Temp ban for {0} until {1}", agentID, until.Value.ToString("s"));
                }
                agentConnector.UpdateAgent(GetAgent);
            }
        }

        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap BanUser(OSDMap map)
        {
            OSDMap resp = new OSDMap();
            resp["Finished"] = OSD.FromBoolean(true);
            UUID agentID = map["UserID"].AsUUID();
            doBan(agentID,null);

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap TempBanUser(OSDMap map)
        {
            OSDMap resp = new OSDMap();
            resp["Finished"] = OSD.FromBoolean(true);
            UUID agentID = map["UserID"].AsUUID();
            DateTime until = map["BannedUntil"].AsDate();
            doBan(agentID, until);

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap UnBanUser(OSDMap map)
        {
            OSDMap resp = new OSDMap();
            resp["Finished"] = OSD.FromBoolean(true);

            UUID agentID = map["UserID"].AsUUID();
            IAgentInfo GetAgent = Aurora.DataManager.DataManager.RequestPlugin<IAgentConnector>().GetAgent(agentID);

            if (GetAgent != null)
            {
                GetAgent.Flags &= IAgentFlags.PermBan;
                GetAgent.Flags &= IAgentFlags.TempBan;
                if (GetAgent.OtherAgentInformation.ContainsKey("TemperaryBanInfo") == true)
                {
                    GetAgent.OtherAgentInformation.Remove("TemperaryBanInfo");
                }
                Aurora.DataManager.DataManager.RequestPlugin<IAgentConnector>().UpdateAgent(GetAgent);
            }

            return resp;
        }

        #endregion

        #endregion

        #region IAbuseReports

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap GetAbuseReports(OSDMap map)
        {
            OSDMap resp = new OSDMap();
            IAbuseReports ar_service = m_registry.RequestModuleInterface<IAbuseReports>();

            int start = map["Start"].AsInteger();
            int count = map["Count"].AsInteger();
            bool active = map["Active"].AsBoolean();

            List<AbuseReport> lar = ar_service.GetAbuseReports(start, count, active);
            OSDArray AbuseReports = new OSDArray();
            foreach (AbuseReport tar in lar)
            {
                AbuseReports.Add(tar.ToOSD());
            }

            resp["AbuseReports"] = AbuseReports;
            resp["Start"] = OSD.FromInteger(start);
            resp["Count"] = OSD.FromInteger(count); // we're not using the AbuseReports.Count because client implementations of the WebAPI can check the count themselves. This is just for showing the input.
            resp["Active"] = OSD.FromBoolean(active);

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap GetAbuseReport(OSDMap map)
        {
            OSDMap resp = new OSDMap();
            IAbuseReports ar_service = m_registry.RequestModuleInterface<IAbuseReports>();
            if (ar_service == null)
            {
                resp["Failed"] = new OSDString("Failed to find IAbuseReports service.");
            }
            else if (!map.ContainsKey("AbuseReport"))
            {
                resp["Failed"] = new OSDString("Abuse Report ID not specified.");
            }
            else
            {
                AbuseReport ar = ar_service.GetAbuseReport(map["AbuseReport"].AsInteger());
                if (ar == null)
                {
                    resp["Failed"] = new OSDString("Failed to find Abuse Report with specified ID.");
                }
                else
                {
                    resp["AbuseReport"] = ar.ToOSD();
                }
            }

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap AbuseReportMarkComplete(OSDMap map)
        {
            OSDMap resp = new OSDMap();
            IAbuseReports ar = m_registry.RequestModuleInterface<IAbuseReports>();
            AbuseReport tar = ar.GetAbuseReport(map["Number"].AsInteger());
            if (tar != null)
            {
                tar.Active = false;
                ar.UpdateAbuseReport(tar);
                resp["Finished"] = OSD.FromBoolean(true);
            }
            else
            {
                resp["Finished"] = OSD.FromBoolean(false);
                resp["Failed"] = OSD.FromString(String.Format("No abuse report found with specified number {0}", map["Number"].AsInteger()));
            }

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap AbuseReportSaveNotes(OSDMap map)
        {
            OSDMap resp = new OSDMap();
            IAbuseReports ar = m_registry.RequestModuleInterface<IAbuseReports>();
            AbuseReport tar = ar.GetAbuseReport(map["Number"].AsInteger());
            if (tar != null)
            {
                tar.Notes = map["Notes"].ToString();
                ar.UpdateAbuseReport(tar);
                resp["Finished"] = OSD.FromBoolean(true);
            }
            else
            {
                resp["Finished"] = OSD.FromBoolean(false);
                resp["Failed"] = OSD.FromString(String.Format("No abuse report found with specified number {0}", map["Number"].AsInteger()));
            }

            return resp;
        }

        #endregion

        #region Places

        #region Estate

        private static OSDMap EstateSettings2WebOSD(EstateSettings ES)
        {
            OSDMap es = ES.ToOSD();

            OSDArray bans = (OSDArray)es["EstateBans"];
            OSDArray Bans = new OSDArray(bans.Count);
            foreach (OSDMap ban in bans)
            {
                Bans.Add(OSD.FromUUID(ban["BannedUserID"]));
            }
            es["EstateBans"] = Bans;

            return es;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap GetEstates(OSDMap map)
        {
            OSDMap resp = new OSDMap(1);
            resp["Estates"] = new OSDArray(0);

            IEstateConnector estates = Aurora.DataManager.DataManager.RequestPlugin<IEstateConnector>();

            if (estates != null && map.ContainsKey("Owner"))
            {
                Dictionary<string, bool> boolFields = new Dictionary<string, bool>();
                if (map.ContainsKey("BoolFields") && map["BoolFields"].Type == OSDType.Map)
                {
                    OSDMap fields = (OSDMap)map["BoolFields"];
                    foreach (string field in fields.Keys)
                    {
                        boolFields[field] = int.Parse(fields[field]) != 0;
                    }
                }

                resp["Estates"] = new OSDArray(estates.GetEstates(map["Owner"].AsUUID(), boolFields).ConvertAll<OSD>(x => EstateSettings2WebOSD(x)));
            }

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap GetEstate(OSDMap map)
        {
            OSDMap resp = new OSDMap(1);
            resp["Failed"] = true;

            IEstateConnector estates = Aurora.DataManager.DataManager.RequestPlugin<IEstateConnector>();
            if (estates != null && map.ContainsKey("Estate"))
            {
                int EstateID;
                EstateSettings es = null;
                if (int.TryParse(map["Estate"], out EstateID))
                {
                    es = estates.GetEstateSettings(map["Estate"].AsInteger());
                }
                else
                {
                    es = estates.GetEstateSettings(map["Estate"].AsString());
                }
                if (es != null)
                {
                    resp.Remove("Failed");
                    resp["Estate"] = EstateSettings2WebOSD(es);
                }
            }

            return resp;
        }

        #endregion

        #region Regions

        private static OSDMap GridRegion2WebOSD(GridRegion region)
        {
            OSDMap regionOSD = region.ToOSD();
            regionOSD["EstateID"] = Aurora.DataManager.DataManager.RequestPlugin<IEstateConnector>().GetEstateID(region.RegionID);
            return regionOSD;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap GetRegions(OSDMap map)
        {
            OSDMap resp = new OSDMap();
            RegionFlags includeFlags = map.ContainsKey("RegionFlags") ? (RegionFlags)map["RegionFlags"].AsInteger() : RegionFlags.RegionOnline;
            RegionFlags excludeFlags = map.ContainsKey("ExcludeRegionFlags") ? (RegionFlags)map["ExcludeRegionFlags"].AsInteger() : 0;
            int start = map.Keys.Contains("Start") ? map["Start"].AsInteger() : 0;
            if (start < 0)
            {
                start = 0;
            }
            int count = map.Keys.Contains("Count") ? map["Count"].AsInteger() : 10;
            if (count < 0)
            {
                count = 1;
            }

            IRegionData regiondata = Aurora.DataManager.DataManager.RequestPlugin<IRegionData>();

            Dictionary<string, bool> sort = new Dictionary<string, bool>();

            string[] supportedSort = new string[3]{
                "SortRegionName",
                "SortLocX",
                "SortLocY"
            };

            foreach (string sortable in supportedSort)
            {
                if (map.ContainsKey(sortable))
                {
                    sort[sortable.Substring(4)] = map[sortable].AsBoolean();
                }
            }

            List<GridRegion> regions = regiondata.Get(includeFlags, excludeFlags, (uint)start, (uint)count, sort);
            OSDArray Regions = new OSDArray();
            foreach (GridRegion region in regions)
            {
                Regions.Add(GridRegion2WebOSD(region));
            }

            MainConsole.Instance.Trace("Total regions: " + regiondata.Count(includeFlags, excludeFlags));

            resp["Start"] = OSD.FromInteger(start);
            resp["Count"] = OSD.FromInteger(count);
            resp["Total"] = OSD.FromInteger((int)regiondata.Count(includeFlags, excludeFlags));
            resp["Regions"] = Regions;
            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap GetRegionsByXY(OSDMap map)
        {
            OSDMap resp = new OSDMap();

            if (!map.ContainsKey("X") || !map.ContainsKey("Y"))
            {
                resp["Failed"] = new OSDString("X and Y coordinates not specified");
            }
            else
            {
                int x = map["X"].AsInteger();
                int y = map["Y"].AsInteger();
                UUID scope = map.ContainsKey("ScopeID") ? UUID.Parse(map["ScopeID"].AsString()) : UUID.Zero;
                RegionFlags include = map.Keys.Contains("RegionFlags") ? (RegionFlags)map["RegionFlags"].AsInteger() : RegionFlags.RegionOnline;
                RegionFlags? exclude = null;
                if (map.Keys.Contains("ExcludeRegionFlags"))
                {
                    exclude = (RegionFlags)map["ExcludeRegionFlags"].AsInteger();
                }

                IRegionData regiondata = Aurora.DataManager.DataManager.RequestPlugin<IRegionData>();

                if (regiondata == null)
                {
                    resp["Failed"] = new OSDString("Could not get IRegionData plugin");
                }
                else
                {
                    List<GridRegion> regions = regiondata.Get(x, y, scope);
                    OSDArray Regions = new OSDArray();
                    foreach (GridRegion region in regions)
                    {
                        if (((int)region.Flags & (int)include) == (int)include && (!exclude.HasValue || ((int)region.Flags & (int)exclude.Value) != (int)exclude))
                        {
                            Regions.Add(GridRegion2WebOSD(region));
                        }
                    }
                    resp["Total"] = Regions.Count;
                    resp["Regions"] = Regions;
                }
            }

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap GetRegionsInArea(OSDMap map)
        {
            OSDMap resp = new OSDMap();

            if (!map.ContainsKey("StartX") || !map.ContainsKey("StartY") || !map.ContainsKey("EndX") || !map.ContainsKey("EndY"))
            {
                resp["Failed"] = new OSDString("Start and End x/y coordinates must be specified");
            }
            else
            {
                int StartX = map["StartX"].AsInteger();
                int StartY = map["StartY"].AsInteger();
                int EndX = map["EndX"].AsInteger();
                int EndY = map["EndY"].AsInteger();

                UUID scope = UUID.Zero;
                if (map.ContainsKey("ScopeID") && !UUID.TryParse(map["ScopeID"].AsString(), out scope))
                {
                    resp["Failed"] = new OSDString("ScopeID was specified but was not valid.");
                    return resp;
                }

                IRegionData regiondata = Aurora.DataManager.DataManager.RequestPlugin<IRegionData>();
                if (regiondata == null)
                {
                    resp["Failed"] = new OSDString("Could not get IRegionData plugin");
                }
                else
                {
                    List<GridRegion> regions = regiondata.Get(StartX, StartY, EndX, EndY, scope);
                    OSDArray Regions = new OSDArray();
                    foreach (GridRegion region in regions)
                    {
                        Regions.Add(GridRegion2WebOSD(region));
                    }
                    resp["Total"] = Regions.Count;
                    resp["Regions"] = Regions;
                }
            }

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap GetRegionsInEstate(OSDMap map)
        {
            OSDMap resp = new OSDMap();

            RegionFlags flags = map.Keys.Contains("RegionFlags") ? (RegionFlags)map["RegionFlags"].AsInteger() : RegionFlags.RegionOnline;
            uint start = map.Keys.Contains("Start") ? map["Start"].AsUInteger() : 0;
            uint count = map.Keys.Contains("Count") ? map["Count"].AsUInteger() : 10;
            Dictionary<string, bool> sort = new Dictionary<string, bool>();
            if (map.ContainsKey("Sort") && map["Sort"].Type == OSDType.Map)
            {
                OSDMap fields = (OSDMap)map["Sort"];
                foreach (string field in fields.Keys)
                {
                    sort[field] = int.Parse(fields[field]) != 0;
                }
            }

            resp["Start"] = OSD.FromInteger(start);
            resp["Count"] = OSD.FromInteger(count);
            resp["Total"] = OSD.FromInteger(0);
            resp["Regions"] = new OSDArray(0);

            IRegionData regiondata = Aurora.DataManager.DataManager.RequestPlugin<IRegionData>();
            if (regiondata != null && map.ContainsKey("Estate"))
            {
                List<GridRegion> regions = regiondata.Get(start, count, map["Estate"].AsUInteger(), flags, sort);
                OSDArray Regions = new OSDArray(regions.Count);
                regions.ForEach(delegate(GridRegion region)
                {
                    Regions.Add(GridRegion2WebOSD(region));
                });
                resp["Total"] = regiondata.Count(map["Estate"].AsUInteger(), flags);
            }

            return resp;
        }

        private GridRegion GetRegionByNameOrUUID(OSDMap map)
        {
            GridRegion region = null;

            IRegionData regiondata = Aurora.DataManager.DataManager.RequestPlugin<IRegionData>();
            if (regiondata != null && (map.ContainsKey("RegionID") || map.ContainsKey("Region")))
            {
                string regionName = map.ContainsKey("Region") ? map["Region"].ToString().Trim() : "";
                UUID regionID = map.ContainsKey("RegionID") ? UUID.Parse(map["RegionID"].ToString()) : UUID.Zero;
                UUID scopeID = map.ContainsKey("ScopeID") ? UUID.Parse(map["ScopeID"].ToString()) : UUID.Zero;
                if (regionID != UUID.Zero)
                {
                    region = regiondata.Get(regionID, scopeID);
                }
                else if (regionName != string.Empty)
                {
                    List<GridRegion> regions = regiondata.Get(regionName, scopeID);
                    region = regions.Count > 0 ? regions[0] : null;
                }
            }

            return region;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap GetRegion(OSDMap map)
        {
            OSDMap resp = new OSDMap();
            GridRegion region = GetRegionByNameOrUUID(map);
            if (region != null)
            {
                resp["Region"] = GridRegion2WebOSD(region);
            }
            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap GetRegionNeighbours(OSDMap map)
        {
            OSDMap resp = new OSDMap();
            IRegionData regiondata = Aurora.DataManager.DataManager.RequestPlugin<IRegionData>();
            if (regiondata != null && map.ContainsKey("RegionID"))
            {
                List<GridRegion> regions = regiondata.GetNeighbours(
                    UUID.Parse(map["RegionID"].ToString()),
                    map.ContainsKey("ScopeID") ? UUID.Parse(map["ScopeID"].ToString()) : UUID.Zero,
                    map.ContainsKey("Range") ? uint.Parse(map["Range"].ToString()) : 128
                );
                OSDArray Regions = new OSDArray(regions.Count);
                foreach (GridRegion region in regions)
                {
                    Regions.Add(GridRegion2WebOSD(region));
                }
                resp["Total"] = Regions.Count;
                resp["Regions"] = Regions;
            }
            return resp;
        }

/// <summary>
/// Attempts to restart the specified region.
/// </summary>
/// <param name="map"></param>
/// <param name="requestingAgentID"></param>
/// <returns></returns>
        [WebAPIMethod(WebAPIHttpMethod.GET, true)]
        private OSDMap RestartRegion(OSDMap map, UUID requestingAgentID)
        {
            OSDMap resp = new OSDMap();

            SceneManager manager = m_registry.RequestModuleInterface<SceneManager>();
            if (manager == null)
            {
                resp["Failed"] = OSD.FromString("Could not get scene manager.");
            }
            else
            {
                GridRegion region = GetRegionByNameOrUUID(map);
                IScene regionScene;
                if (region == null)
                {
                    resp["Failed"] = OSD.FromString("Region does not appear to exist.");
                }
                else if (!manager.TryGetScene(region.RegionID, out regionScene))
                {
                    resp["Failed"] = OSD.FromString("Region was found in data store but could not be found via scene manager.");
                }
                else
                {
                    IRestartModule restartModule = regionScene.RequestModuleInterface<IRestartModule>();
                    if (restartModule == null)
                    {
                        resp["Failed"] = OSD.FromString("Could not get IRestartModule from region scene.");
                    }
                    else
                    {
                        restartModule.RestartScene();
                        resp["Success"] = OSD.FromBoolean(true);
                    }
                }
            }

            return resp;
        }

        #endregion

        #region Parcels

        private static OSDMap LandData2WebOSD(LandData parcel)
        {
            OSDMap parcelOSD = parcel.ToOSD();
            parcelOSD["GenericData"] = parcelOSD.ContainsKey("GenericData") ? (parcelOSD["GenericData"].Type == OSDType.Map ? parcelOSD["GenericData"] : (OSDMap)OSDParser.DeserializeLLSDXml(parcelOSD["GenericData"].ToString())) : new OSDMap();
            parcelOSD["Bitmap"] = OSD.FromBinary(parcelOSD["Bitmap"]).ToString();
            parcelOSD["RegionHandle"] = OSD.FromString((parcelOSD["RegionHandle"].AsULong()).ToString());
            parcelOSD["AuctionID"] = OSD.FromInteger((int)parcelOSD["AuctionID"].AsUInteger());
            return parcelOSD;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap GetParcelsByRegion(OSDMap map)
        {
            OSDMap resp = new OSDMap();
            resp["Parcels"] = new OSDArray();
            resp["Total"] = OSD.FromInteger(0);

            IDirectoryServiceConnector directory = Aurora.DataManager.DataManager.RequestPlugin<IDirectoryServiceConnector>();

            if (directory != null && map.ContainsKey("Region") == true)
            {
                UUID RegionID = UUID.Parse(map["Region"]);
                UUID ScopeID = map.ContainsKey("ScopeID") ? UUID.Parse(map["ScopeID"].ToString()) : UUID.Zero;
                UUID owner = map.ContainsKey("Owner") ? UUID.Parse(map["Owner"].ToString()) : UUID.Zero;
                uint start = map.ContainsKey("Start") ? uint.Parse(map["Start"].ToString()) : 0;
                uint count = map.ContainsKey("Count") ? uint.Parse(map["Count"].ToString()) : 10;
                ParcelFlags flags = map.ContainsKey("Flags") ? (ParcelFlags)int.Parse(map["Flags"].ToString()) : ParcelFlags.None;
                ParcelCategory category = map.ContainsKey("Category") ? (ParcelCategory)uint.Parse(map["Flags"].ToString()) : ParcelCategory.Any;
                uint total = directory.GetNumberOfParcelsByRegion(RegionID, ScopeID, owner, flags, category);
                if (total > 0)
                {
                    resp["Total"] = OSD.FromInteger((int)total);
                    if (count == 0)
                    {
                        return resp;
                    }
                    List<LandData> parcels = directory.GetParcelsByRegion(start, count, RegionID, ScopeID, owner, flags, category);
                    OSDArray Parcels = new OSDArray(parcels.Count);
                    parcels.ForEach(delegate(LandData parcel)
                    {
                        Parcels.Add(LandData2WebOSD(parcel));
                    });
                    resp["Parcels"] = Parcels;
                }
            }

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap GetParcelsWithNameByRegion(OSDMap map)
        {
            OSDMap resp = new OSDMap();
            resp["Parcels"] = new OSDArray();
            resp["Total"] = OSD.FromInteger(0);

            IDirectoryServiceConnector directory = Aurora.DataManager.DataManager.RequestPlugin<IDirectoryServiceConnector>();
            if (directory != null && map.ContainsKey("Region") && map.ContainsKey("Parcel"))
            {
                UUID RegionID = UUID.Parse(map["Region"]);
                string name = map["Parcel"].ToString().Trim();
                UUID ScopeID = map.ContainsKey("ScopeID") ? UUID.Parse(map["ScopeID"].ToString()) : UUID.Zero;
                uint start = map.ContainsKey("Start") ? uint.Parse(map["Start"].ToString()) : 0;
                uint count = map.ContainsKey("Count") ? uint.Parse(map["Count"].ToString()) : 10;

                if (name == string.Empty)
                {
                    MainConsole.Instance.Trace("Parcel name was an empty string.");
                }
                else
                {
                    uint total = directory.GetNumberOfParcelsWithNameByRegion(RegionID, ScopeID, name);
                    if (total > 0)
                    {
                        resp["Total"] = OSD.FromInteger((int)total);
                        if (count == 0)
                        {
                            return resp;
                        }
                        List<LandData> parcels = directory.GetParcelsWithNameByRegion(start, count, RegionID, ScopeID, name);
                        OSDArray Parcels = new OSDArray(parcels.Count);
                        parcels.ForEach(delegate(LandData parcel)
                        {
                            Parcels.Add(LandData2WebOSD(parcel));
                        });
                        resp["Parcels"] = Parcels;
                    }
                }
            }
            else
            {
                if (directory == null)
                {
                    MainConsole.Instance.Trace("Could not find IDirectoryServiceConnector");
                }
                else if (!map.ContainsKey("Region"))
                {
                    MainConsole.Instance.Trace("Region was not specified.");
                }
                else if (!map.ContainsKey("Parcel"))
                {
                    MainConsole.Instance.Trace("Parcel was not specified.");
                }
            }

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap GetParcel(OSDMap map)
        {
            OSDMap resp = new OSDMap();

            UUID regionID = map.ContainsKey("RegionID") ? UUID.Parse(map["RegionID"].ToString()) : UUID.Zero;
            UUID scopeID = map.ContainsKey("ScopeID") ? UUID.Parse(map["ScopeID"].ToString()) : UUID.Zero;
            UUID parcelID = map.ContainsKey("ParcelInfoUUID") ? UUID.Parse(map["ParcelInfoUUID"].ToString()) : UUID.Zero;
            string parcelName = map.ContainsKey("Parcel") ? map["Parcel"].ToString().Trim() : string.Empty;

            IDirectoryServiceConnector directory = Aurora.DataManager.DataManager.RequestPlugin<IDirectoryServiceConnector>();

            if (directory != null && (parcelID != UUID.Zero || (regionID != UUID.Zero && parcelName != string.Empty)))
            {
                LandData parcel = null;

                if (parcelID != UUID.Zero)
                {
                    parcel = directory.GetParcelInfo(parcelID);
                }
                else if (regionID != UUID.Zero && parcelName != string.Empty)
                {
                    parcel = directory.GetParcelInfo(regionID, scopeID, parcelName);
                }

                if (parcel != null)
                {
                    resp["Parcel"] = LandData2WebOSD(parcel);
                }
            }

            return resp;
        }

        #endregion

        #endregion

        #region Groups

        #region GroupRecord

        private static OSDMap GroupRecord2OSDMap(GroupRecord group)
        {
            OSDMap resp = new OSDMap();
            resp["GroupID"] = group.GroupID;
            resp["GroupName"] = group.GroupName;
            resp["AllowPublish"] = group.AllowPublish;
            resp["MaturePublish"] = group.MaturePublish;
            resp["Charter"] = group.Charter;
            resp["FounderID"] = group.FounderID;
            resp["GroupPicture"] = group.GroupPicture;
            resp["MembershipFee"] = group.MembershipFee;
            resp["OpenEnrollment"] = group.OpenEnrollment;
            resp["OwnerRoleID"] = group.OwnerRoleID;
            resp["ShowInList"] = group.ShowInList;
            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap GroupAsNewsSource(OSDMap map)
        {
            OSDMap resp = new OSDMap();
            resp["Verified"] = OSD.FromBoolean(false);
            IGenericsConnector generics = Aurora.DataManager.DataManager.RequestPlugin<IGenericsConnector>();
            UUID groupID;
            if (generics != null && map.ContainsKey("Group") == true && map.ContainsKey("Use") && UUID.TryParse(map["Group"], out groupID) == true)
            {
                if (map["Use"].AsBoolean())
                {
                    OSDMap useValue = new OSDMap();
                    useValue["Use"] = OSD.FromBoolean(true);
                    generics.AddGeneric(groupID, "Group", "WebUI_newsSource", useValue);
                }
                else
                {
                    generics.RemoveGeneric(groupID, "Group", "WebUI_newsSource");
                }
                resp["Verified"] = OSD.FromBoolean(true);
            }
            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET, true)]
        private OSDMap GetGroups(OSDMap map, UUID requestingAgentID)
        {
            OSDMap resp = new OSDMap();
            uint start = map.ContainsKey("Start") ? map["Start"].AsUInteger() : 0;
            resp["Start"] = start;
            resp["Total"] = 0;

            IGroupsServiceConnector groups = Aurora.DataManager.DataManager.RequestPlugin<IGroupsServiceConnector>();
            OSDArray Groups = new OSDArray();
            if (groups != null)
            {
                if (!map.ContainsKey("GroupIDs"))
                {
                    Dictionary<string, bool> sort = new Dictionary<string, bool>();
                    Dictionary<string, bool> boolFields = new Dictionary<string, bool>();

                    if (map.ContainsKey("Sort") && map["Sort"].Type == OSDType.Map)
                    {
                        OSDMap fields = (OSDMap)map["Sort"];
                        foreach (string field in fields.Keys)
                        {
                            sort[field] = int.Parse(fields[field]) != 0;
                        }
                    }
                    if (map.ContainsKey("BoolFields") && map["BoolFields"].Type == OSDType.Map)
                    {
                        OSDMap fields = (OSDMap)map["BoolFields"];
                        foreach (string field in fields.Keys)
                        {
                            boolFields[field] = int.Parse(fields[field]) != 0;
                        }
                    }
                    List<GroupRecord> reply = groups.GetGroupRecords(
                        requestingAgentID,
                        start,
                        map.ContainsKey("Count") ? map["Count"].AsUInteger() : 10,
                        sort,
                        boolFields
                    );
                    if (reply.Count > 0)
                    {
                        foreach (GroupRecord groupReply in reply)
                        {
                            Groups.Add(GroupRecord2OSDMap(groupReply));
                        }
                    }
                    resp["Total"] = groups.GetNumberOfGroups(requestingAgentID, boolFields);
                }
                else
                {
                    OSDArray groupIDs = (OSDArray)map["Groups"];
                    List<UUID> GroupIDs = new List<UUID>();
                    foreach (string groupID in groupIDs)
                    {
                        UUID foo;
                        if (UUID.TryParse(groupID, out foo))
                        {
                            GroupIDs.Add(foo);
                        }
                    }
                    if (GroupIDs.Count > 0)
                    {
                        List<GroupRecord> reply = groups.GetGroupRecords(requestingAgentID, GroupIDs);
                        if (reply.Count > 0)
                        {
                            foreach (GroupRecord groupReply in reply)
                            {
                                Groups.Add(GroupRecord2OSDMap(groupReply));
                            }
                        }
                        resp["Total"] = Groups.Count;
                    }
                }
            }

            resp["Groups"] = Groups;
            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET, true)]
        private OSDMap GetNewsSources(OSDMap map, UUID requestingAgentID)
        {
            OSDMap resp = new OSDMap();
            uint start = map.ContainsKey("Start") ? map["Start"].AsUInteger() : 0;
            uint count = map.ContainsKey("Count") ? map["Count"].AsUInteger() : 10;
            IGenericsConnector generics = Aurora.DataManager.DataManager.RequestPlugin<IGenericsConnector>();
            IGroupsServiceConnector groups = Aurora.DataManager.DataManager.RequestPlugin<IGroupsServiceConnector>();

            if (generics == null)
            {
                resp["Failed"] = new OSDString("Could not find IGenericsConnector");
            }
            else if (groups == null)
            {
                resp["Failed"] = new OSDString("Could not find IGroupsServiceConnector");
            }
            else
            {
                OSDMap useValue = new OSDMap();
                useValue["Use"] = OSD.FromBoolean(true);
                List<UUID> GroupIDs = generics.GetOwnersByGeneric("Group", "WebUI_newsSource", useValue);
                resp["Total"] = GroupIDs.Count;
                resp["Start"] = (int)start;
                resp["Count"] = (int)count;

                OSDArray Groups = new OSDArray();
                if (start < GroupIDs.Count)
                {
                    int end = (int)count;
                    if (start + count > GroupIDs.Count)
                    {
                        end = GroupIDs.Count - (int)start;
                    }
                    List<UUID> page = GroupIDs.GetRange((int)start, end);
                    if (page.Count > 0)
                    {
                        List<GroupRecord> reply = groups.GetGroupRecords(requestingAgentID, page);
                        if (reply.Count > 0)
                        {
                            foreach (GroupRecord groupReply in reply)
                            {
                                Groups.Add(GroupRecord2OSDMap(groupReply));
                            }
                        }
                    }
                }
                resp["Groups"] = Groups;
            }


            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET, true)]
        private OSDMap GetGroup(OSDMap map, UUID requestingAgentID)
        {
            OSDMap resp = new OSDMap();
            IGroupsServiceConnector groups = Aurora.DataManager.DataManager.RequestPlugin<IGroupsServiceConnector>();
            resp["Group"] = false;
            if (groups != null && (map.ContainsKey("Name") || map.ContainsKey("UUID")))
            {
                UUID groupID = map.ContainsKey("UUID") ? UUID.Parse(map["UUID"].ToString()) : UUID.Zero;
                string name = map.ContainsKey("Name") ? map["Name"].ToString() : "";
                GroupRecord reply = groups.GetGroupRecord(requestingAgentID, groupID, name);
                if (reply != null)
                {
                    resp["Group"] = GroupRecord2OSDMap(reply);
                }
            }
            return resp;
        }

        #endregion

        #region GroupNoticeData

        [WebAPIMethod(WebAPIHttpMethod.GET, true)]
        private OSDMap GroupNotices(OSDMap map, UUID requestingAgentID)
        {
            OSDMap resp = new OSDMap();
            resp["GroupNotices"] = new OSDArray();
            resp["Total"] = 0;
            IGroupsServiceConnector groups = Aurora.DataManager.DataManager.RequestPlugin<IGroupsServiceConnector>();

            if (map.ContainsKey("Groups") && groups != null && map["Groups"].Type.ToString() == "Array")
            {
                OSDArray groupIDs = (OSDArray)map["Groups"];
                List<UUID> GroupIDs = new List<UUID>();
                foreach (string groupID in groupIDs)
                {
                    UUID foo;
                    if (UUID.TryParse(groupID, out foo))
                    {
                        GroupIDs.Add(foo);
                    }
                }
                if (GroupIDs.Count > 0)
                {
                    uint start = map.ContainsKey("Start") ? uint.Parse(map["Start"]) : 0;
                    uint count = map.ContainsKey("Count") ? uint.Parse(map["Count"]) : 10;
                    List<GroupNoticeData> groupNotices = groups.GetGroupNotices(requestingAgentID, start, count, GroupIDs);
                    OSDArray GroupNotices = new OSDArray(groupNotices.Count);
                    groupNotices.ForEach(delegate(GroupNoticeData GND)
                    {
                        OSDMap gnd = new OSDMap();
                        gnd["GroupID"] = OSD.FromUUID(GND.GroupID);
                        gnd["NoticeID"] = OSD.FromUUID(GND.NoticeID);
                        gnd["Timestamp"] = OSD.FromInteger((int)GND.Timestamp);
                        gnd["FromName"] = OSD.FromString(GND.FromName);
                        gnd["Subject"] = OSD.FromString(GND.Subject);
                        gnd["HasAttachment"] = OSD.FromBoolean(GND.HasAttachment);
                        gnd["ItemID"] = OSD.FromUUID(GND.ItemID);
                        gnd["AssetType"] = OSD.FromInteger((int)GND.AssetType);
                        gnd["ItemName"] = OSD.FromString(GND.ItemName);
                        GroupNoticeInfo notice = groups.GetGroupNotice(requestingAgentID, GND.NoticeID);
                        gnd["Message"] = OSD.FromString(notice.Message);
                        GroupNotices.Add(gnd);
                    });
                    resp["GroupNotices"] = GroupNotices;
                    resp["Total"] = (int)groups.GetNumberOfGroupNotices(requestingAgentID, GroupIDs);
                }
            }

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.GET, true)]
        private OSDMap NewsFromGroupNotices(OSDMap map, UUID requestingAgentID)
        {
            OSDMap resp = new OSDMap();
            resp["GroupNotices"] = new OSDArray();
            resp["Total"] = 0;
            IGenericsConnector generics = Aurora.DataManager.DataManager.RequestPlugin<IGenericsConnector>();
            IGroupsServiceConnector groups = Aurora.DataManager.DataManager.RequestPlugin<IGroupsServiceConnector>();
            if (generics == null || groups == null)
            {
                return resp;
            }
            OSDMap useValue = new OSDMap();
            useValue["Use"] = OSD.FromBoolean(true);
            List<UUID> GroupIDs = generics.GetOwnersByGeneric("Group", "WebUI_newsSource", useValue);
            if (GroupIDs.Count <= 0)
            {
                return resp;
            }
            foreach (UUID groupID in GroupIDs)
            {
                GroupRecord group = groups.GetGroupRecord(requestingAgentID, groupID, "");
                if (!group.ShowInList)
                {
                    GroupIDs.Remove(groupID);
                }
            }

            uint start = map.ContainsKey("Start") ? uint.Parse(map["Start"].ToString()) : 0;
            uint count = map.ContainsKey("Count") ? uint.Parse(map["Count"].ToString()) : 10;

            OSDMap args = new OSDMap();
            args["Start"] = OSD.FromString(start.ToString());
            args["Count"] = OSD.FromString(count.ToString());
            args["Groups"] = new OSDArray(GroupIDs.ConvertAll(x => OSD.FromString(x.ToString())));

            return GroupNotices(args, requestingAgentID);
        }

        [WebAPIMethod(WebAPIHttpMethod.GET, true)]
        private OSDMap GetGroupNotice(OSDMap map, UUID requestingAgentID)
        {
            OSDMap resp = new OSDMap();
            UUID noticeID = map.ContainsKey("NoticeID") ? UUID.Parse(map["NoticeID"]) : UUID.Zero;
            IGroupsServiceConnector groups = Aurora.DataManager.DataManager.RequestPlugin<IGroupsServiceConnector>();

            if (noticeID != UUID.Zero && groups != null)
            {
                GroupNoticeData GND = groups.GetGroupNoticeData(requestingAgentID, noticeID);
                if (GND != null)
                {
                    OSDMap gnd = new OSDMap();
                    gnd["GroupID"] = OSD.FromUUID(GND.GroupID);
                    gnd["NoticeID"] = OSD.FromUUID(GND.NoticeID);
                    gnd["Timestamp"] = OSD.FromInteger((int)GND.Timestamp);
                    gnd["FromName"] = OSD.FromString(GND.FromName);
                    gnd["Subject"] = OSD.FromString(GND.Subject);
                    gnd["HasAttachment"] = OSD.FromBoolean(GND.HasAttachment);
                    gnd["ItemID"] = OSD.FromUUID(GND.ItemID);
                    gnd["AssetType"] = OSD.FromInteger((int)GND.AssetType);
                    gnd["ItemName"] = OSD.FromString(GND.ItemName);
                    GroupNoticeInfo notice = groups.GetGroupNotice(requestingAgentID, GND.NoticeID);
                    gnd["Message"] = OSD.FromString(notice.Message);

                    resp["GroupNotice"] = gnd;
                }
            }

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.POST, true)]
        private OSDMap EditGroupNotice(OSDMap map, UUID requestingAgentID)
        {
            OSDMap resp = new OSDMap();
            UUID noticeID = map.ContainsKey("NoticeID") ? UUID.Parse(map["NoticeID"]) : UUID.Zero;
            IGroupsServiceConnector groups = Aurora.DataManager.DataManager.RequestPlugin<IGroupsServiceConnector>();
            GroupNoticeData GND = noticeID != UUID.Zero && groups != null ? groups.GetGroupNoticeData(requestingAgentID, noticeID) : null;
            GroupNoticeInfo notice = GND != null ? groups.GetGroupNotice(requestingAgentID, GND.NoticeID) : null;

            if (noticeID == UUID.Zero)
            {
                resp["Failed"] = new OSDString("No notice ID was specified");
            }
            else if (groups == null)
            {
                resp["Failed"] = new OSDString("Could not find IGroupsServiceConnector");
            }
            else if (GND == null || notice == null)
            {
                resp["Failed"] = new OSDString("Could not find group notice with specified ID");
            }
            else if (!map.ContainsKey("Subject") && !map.ContainsKey("Message"))
            {
                resp["Success"] = new OSDBoolean(false);
                resp["Note"] = new OSDString("No changes were made to the group notice");
            }
            else
            {
                resp["Success"] = groups.EditGroupNotice(requestingAgentID, notice.GroupID, GND.NoticeID, map.ContainsKey("Subject") ? map["Subject"].ToString() : GND.Subject, map.ContainsKey("Message") ? map["Message"].ToString() : notice.Message);
            }

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap AddGroupNotice(OSDMap map)
        {
            OSDMap resp = new OSDMap();

            if (!map.ContainsKey("GroupID") || !map.ContainsKey("AuthorID") || !map.ContainsKey("Subject") || !map.ContainsKey("Message"))
            {
                resp["Failed"] = new OSDString("Missing required arguments one or more of GroupID, AuthorID, Subject, Message");
            }
            else
            {
                UUID GroupID = UUID.Zero;
                UUID.TryParse(map["GroupID"].ToString(), out GroupID);

                UUID AuthorID = UUID.Zero;
                UUID.TryParse(map["AuthorID"].ToString(), out AuthorID);

                string subject = map["Subject"].ToString().Trim();
                string message = map["Message"].ToString().Trim();

                IGroupsServiceConnector groups = Aurora.DataManager.DataManager.RequestPlugin<IGroupsServiceConnector>();
                IUserAccountService users = m_registry.RequestModuleInterface<IUserAccountService>();
                UserAccount Author = AuthorID != UUID.Zero && users != null ? users.GetUserAccount(UUID.Zero, AuthorID) : null;

                if (GroupID == UUID.Zero)
                {
                    resp["Failed"] = new OSDString("GroupID was UUID.Zero");
                }
                else if (AuthorID == UUID.Zero)
                {
                    resp["Failed"] = new OSDString("AuthorID was UUID.Zero");
                }
                else if (subject == string.Empty)
                {
                    resp["Failed"] = new OSDString("Subject was empty");
                }
                else if (message == string.Empty)
                {
                    resp["Failed"] = new OSDString("Message was empty");
                }
                else if (groups == null)
                {
                    resp["Failed"] = new OSDString("Could not findIGroupsServiceConnector");
                }
                else if (users == null)
                {
                    resp["Failed"] = new OSDString("Could not find IUserAccountService");
                }
                else if (Author == null)
                {
                    resp["Failed"] = new OSDString(string.Format("Could not find author with ID {0}", AuthorID));
                }
                else
                {
                    UUID noticeID = UUID.Random();
                    try
                    {
                        groups.AddGroupNotice(AuthorID, GroupID, noticeID, Author.Name, subject, message, UUID.Zero, 0, "");
                        resp["NoticeID"] = noticeID;
                    }
                    catch
                    {
                        resp["Failed"] = new OSDString("An exception was thrown.");
                    }
                }
            }

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.POST, true)]
        private OSDMap RemoveGroupNotice(OSDMap map, UUID requestingAgentID)
        {
            OSDMap resp = new OSDMap();

            if (!map.ContainsKey("GroupID") || !map.ContainsKey("NoticeID"))
            {
                resp["Failed"] = new OSDString("Missing required arguments one or more of GroupID, NoticeID");
            }
            else
            {
                UUID GroupID = UUID.Zero;
                UUID.TryParse(map["GroupID"].ToString(), out GroupID);

                UUID noticeID = UUID.Zero;
                UUID.TryParse(map["NoticeID"].ToString(), out noticeID);

                IGroupsServiceConnector groups = Aurora.DataManager.DataManager.RequestPlugin<IGroupsServiceConnector>();

                if (GroupID == UUID.Zero)
                {
                    resp["Failed"] = new OSDString("GroupID was UUID.Zero");
                }
                else if (noticeID == UUID.Zero)
                {
                    resp["Failed"] = new OSDString("NoticeID was UUID.Zero");
                }
                else if (groups == null)
                {
                    resp["Failed"] = new OSDString("Could not findIGroupsServiceConnector");
                }
                else
                {
                    try
                    {
                        resp["Success"] = groups.RemoveGroupNotice(requestingAgentID, GroupID, noticeID);
                    }
                    catch
                    {
                        resp["Failed"] = new OSDString("An exception was thrown.");
                    }
                }
            }

            return resp;
        }

        #endregion

        #endregion

        #region Events

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap GetEvents(OSDMap map)
        {
            uint start = map.ContainsKey("Start") ? map["Start"].AsUInteger() : 0;
            uint count = map.ContainsKey("Count") ? map["Count"].AsUInteger() : 0;
            Dictionary<string, bool> sort = new Dictionary<string, bool>();
            Dictionary<string, object> filter = new Dictionary<string, object>();

            OSDMap resp = new OSDMap();
            resp["Start"] = start;
            resp["Total"] = 0;
            resp["Events"] = new OSDArray(0);

            IDirectoryServiceConnector directory = Aurora.DataManager.DataManager.RequestPlugin<IDirectoryServiceConnector>();
            if (directory != null)
            {
                if (map.ContainsKey("Filter") && map["Filter"].Type == OSDType.Map)
                {
                    OSDMap fields = (OSDMap)map["Filter"];
                    foreach (string field in fields.Keys)
                    {
                        filter[field] = fields[field];
                    }
                }
                if (count > 0)
                {
                    if (map.ContainsKey("Sort") && map["Sort"].Type == OSDType.Map)
                    {
                        OSDMap fields = (OSDMap)map["Sort"];
                        foreach (string field in fields.Keys)
                        {
                            sort[field] = int.Parse(fields[field]) != 0;
                        }
                    }

                    OSDArray Events = new OSDArray();
                    directory.GetEvents(start, count, sort, filter).ForEach(delegate(EventData Event)
                    {
                        Events.Add(Event.ToOSD());
                    });
                    resp["Events"] = Events;
                }
                resp["Total"] = (int)directory.GetNumberOfEvents(filter);
            }

            return resp;
        }

        [WebAPIMethod(WebAPIHttpMethod.POST)]
        private OSDMap CreateEvent(OSDMap map)
        {
            OSDMap resp = new OSDMap(1);

            IDirectoryServiceConnector directory = Aurora.DataManager.DataManager.RequestPlugin<IDirectoryServiceConnector>();
            if (directory != null && (
                map.ContainsKey("Creator") && 
                map.ContainsKey("Region") && 
                map.ContainsKey("Date") && 
                map.ContainsKey("Cover") && 
                map.ContainsKey("Maturity") && 
                map.ContainsKey("EventFlags") && 
                map.ContainsKey("Duration") && 
                map.ContainsKey("Position") && 
                map.ContainsKey("Name") && 
                map.ContainsKey("Description") && 
                map.ContainsKey("Category")
            )){
                EventData eventData = directory.CreateEvent(
                    map["Creator"].AsUUID(),
                    map["Region"].AsUUID(),
                    map.ContainsKey("Parcel") ? map["Parcel"].AsUUID() : UUID.Zero,
                    map["Date"].AsDate(),
                    map["Cover"].AsUInteger(),
                    (EventFlags)map["Maturity"].AsUInteger(),
                    map["EventFlags"].AsUInteger() | map["Maturity"].AsUInteger(),
                    map["Duration"].AsUInteger(),
                    Vector3.Parse(map["Position"].AsString()),
                    map["Name"].AsString(),
                    map["Description"].AsString(),
                    map["Category"].AsString()
                );

                if (eventData != null)
                {
                    resp["Event"] = eventData.ToOSD();
                }
            }

            return resp;
        }

        #endregion

        #region Textures

        [WebAPIMethod(WebAPIHttpMethod.GET)]
        private OSDMap SizeOfHTTPGetTextureImage(OSDMap map)
        {
            OSDMap resp = new OSDMap(1);
            resp["Size"] = OSD.FromUInteger(0);

            if (map.ContainsKey("Texture"))
            {
                Hashtable args = new Hashtable(2);
                args["method"] = "GridTexture";
                args["uuid"] = UUID.Parse(map["Texture"].ToString());
                Hashtable texture = OnHTTPGetTextureImage(args);
                if (texture.ContainsKey("str_response_string"))
                {
                    resp["Size"] = OSD.FromInteger(Convert.FromBase64String(texture["str_response_string"].ToString()).Length);
                }
            }

            return resp;
        }

        #endregion

        #endregion
    }
}
