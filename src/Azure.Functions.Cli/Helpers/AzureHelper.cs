using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Azure.Functions.Cli.Arm;
using Azure.Functions.Cli.Arm.Models;
using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.Extensions;
using Colors.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static Azure.Functions.Cli.Common.OutputTheme;

namespace Azure.Functions.Cli.Helpers
{
    public static class AzureHelper
    {
        private static string _storageApiVersion = "2018-02-01";

        public static async Task<Site> GetFunctionApp(string name, string accessToken)
        {
            var subscriptions = await GetSubscriptions(accessToken);
            foreach (var subscription in subscriptions.value)
            {
                var functionApps = await ArmHttpAsync<ArmArrayWrapper<ArmGenericResource>>(
                HttpMethod.Get,
                ArmUriTemplates.SubscriptionResourceByNameAndType.Bind(new
                {
                    subscriptionId = subscription.subscriptionId,
                    resourceType = "Microsoft.Web/sites",
                    resourceName = name
                }),
                accessToken);

                if (functionApps.value.Any())
                {
                    var app = new Site(functionApps.value.First().id);
                    await LoadFunctionApp(app, accessToken);
                    return app;
                }
            }

            throw new ArmResourceNotFoundException($"Can't find app with name \"{name}\"");
        }

        internal static Task<IEnumerable<FunctionInfo>> GetFunctions(Site functionApp, string accessToken)
        {
            var url = new Uri($"{ArmUriTemplates.ArmUrl}{functionApp.SiteId}/hostruntime/admin/functions?api-version={ArmUriTemplates.WebsitesApiVersion}");
            return ArmHttpAsync<IEnumerable<FunctionInfo>>(HttpMethod.Get, url, accessToken);
        }

        private static async Task<Site> LoadFunctionApp(Site site, string accessToken)
        {
            await new[]
            {
                LoadSiteObjectAsync(site, accessToken),
                LoadSitePublishingCredentialsAsync(site, accessToken),
                LoadSiteConfigAsync(site, accessToken),
                LoadAppSettings(site, accessToken),
                LoadConnectionStrings(site, accessToken)
            }
            //.IgnoreFailures()
            .WhenAll();
            return site;
        }

        private async static Task<Site> LoadConnectionStrings(Site site, string accessToken)
        {
            var url = new Uri($"{ArmUriTemplates.ArmUrl}{site.SiteId}/config/ConnectionStrings/list?api-version={ArmUriTemplates.WebsitesApiVersion}");
            var armResponse = await ArmHttpAsync<ArmWrapper<Dictionary<string, AppServiceConnectionString>>>(HttpMethod.Post, url, accessToken);
            site.ConnectionStrings = armResponse.properties;
            return site;
        }

        private async static Task<Site> LoadAppSettings(Site site, string accessToken)
        {
            var url = new Uri($"{ArmUriTemplates.ArmUrl}{site.SiteId}/config/AppSettings/list?api-version={ArmUriTemplates.WebsitesApiVersion}");
            var armResponse = await ArmHttpAsync<ArmWrapper<Dictionary<string, string>>>(HttpMethod.Post, url, accessToken);
            site.AzureAppSettings = armResponse.properties;
            return site;
        }

        public static async Task<Site> LoadSitePublishingCredentialsAsync(Site site, string accessToken)
        {
            var url = new Uri($"{ArmUriTemplates.ArmUrl}{site.SiteId}/config/PublishingCredentials/list?api-version={ArmUriTemplates.WebsitesApiVersion}");
            return site.MergeWith(
                        await ArmHttpAsync<ArmWrapper<ArmWebsitePublishingCredentials>>(
                            HttpMethod.Post,
                            url,
                            accessToken),
                        t => t.properties
                    );
        }

        public static async Task<StorageAccount> GetStorageAccount(string storageAccountName, string accessToken)
        {
            var subscriptions = await GetSubscriptions(accessToken);
            foreach (var subscription in subscriptions.value)
            {
                var storageAccount =
                    await ArmHttpAsync<ArmArrayWrapper<ArmGenericResource>>(
                        HttpMethod.Get,
                        ArmUriTemplates.SubscriptionResourceByNameAndType.Bind(new
                        {
                            subscriptionId = subscription.subscriptionId,
                            resourceName = storageAccountName,
                            resourceType = "Microsoft.Storage/storageAccounts"
                        }),
                        accessToken);

                if (storageAccount.value.Any())
                {
                    return await GetStorageAccount(storageAccount.value.First(), accessToken);
                }
            }

            throw new ArmResourceNotFoundException($"Cannot find storage account with name {storageAccountName}");
        }

        private static async Task<StorageAccount> GetStorageAccount(ArmWrapper<ArmGenericResource> armWrapper, string accessToken)
        {
            try
            {
                var url = new Uri($"{ArmUriTemplates.ArmUrl}{armWrapper.id}/listKeys?api-version={_storageApiVersion}");
                var keys = await ArmHttpAsync<ArmStorageKeysArray>(HttpMethod.Post, url, accessToken);
                return new StorageAccount
                {
                    StorageAccountName = armWrapper.name,
                    StorageAccountKey = keys.keys.First().value
                };
            }
            catch (Exception e)
            {
                if (StaticSettings.IsDebug)
                {
                    ColoredConsole.Error.WriteLine(ErrorColor(e.ToString()));
                }

                throw new CliException($"Cannot get keys for storage account {armWrapper.name}. Make sure you have access to the storage account.");
            }
        }

        internal static Task<ArmSubscriptionsArray> GetSubscriptions(string accessToken)
        {
            var url = new Uri($"{ArmUriTemplates.ArmUrl}/subscriptions?api-version={ArmUriTemplates.ArmApiVersion}");
            return ArmHttpAsync<ArmSubscriptionsArray>(
                HttpMethod.Get,
                url,
                accessToken);
        }

        public static async Task<Site> LoadSiteConfigAsync(Site site, string accessToken)
        {
            var url = new Uri($"{ArmUriTemplates.ArmUrl}{site.SiteId}/config/web?api-version={ArmUriTemplates.WebsitesApiVersion}");
            return site.MergeWith(
                  await ArmHttpAsync<ArmWrapper<ArmWebsiteConfig>>(HttpMethod.Get, url, accessToken),
                  t => t.properties
              );
        }

        public static Task<HttpResponseMessage> SyncTriggers(Site functionApp, string accessToken)
        {
            var url = new Uri($"{ArmUriTemplates.ArmUrl}{functionApp.SiteId}/hostruntime/admin/host/synctriggers?api-version={ArmUriTemplates.WebsitesApiVersion}");
            return ArmClient.HttpInvoke(HttpMethod.Post, url, accessToken);
        }

        public static async Task<Site> LoadSiteObjectAsync(Site site, string accessToken)
        {
            var url = new Uri($"{ArmUriTemplates.ArmUrl}{site.SiteId}?api-version={ArmUriTemplates.WebsitesApiVersion}");
            var armSite = await ArmHttpAsync<ArmWrapper<ArmWebsite>>(HttpMethod.Get, url, accessToken);

            site.HostName = armSite.properties.enabledHostNames.FirstOrDefault(s => s.IndexOf(".scm.", StringComparison.OrdinalIgnoreCase) == -1);
            site.ScmUri = armSite.properties.enabledHostNames.FirstOrDefault(s => s.IndexOf(".scm.", StringComparison.OrdinalIgnoreCase) != -1);
            site.Location = armSite.location;
            site.Kind = armSite.kind;
            site.Sku = armSite.properties.sku;
            return site;
        }

        private static async Task<T> ArmHttpAsync<T>(HttpMethod method, Uri uri, string accessToken, object payload = null)
        {
            var response = await ArmClient.HttpInvoke(method, uri, accessToken, payload, retryCount: 3);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsAsync<T>();
        }

        private static async Task ArmHttpAsync(HttpMethod method, Uri uri, string accessToken, object payload = null)
        {
            var response = await ArmClient.HttpInvoke(method, uri, accessToken, payload, retryCount: 3);
            response.EnsureSuccessStatusCode();
        }

        public static async Task<HttpResult<Dictionary<string, string>, string>> UpdateFunctionAppAppSettings(Site site, string accessToken)
        {
            var url = new Uri($"{ArmUriTemplates.ArmUrl}{site.SiteId}/config/AppSettings?api-version={ArmUriTemplates.WebsitesApiVersion}");
            var response = await ArmClient.HttpInvoke(HttpMethod.Put, url, accessToken, new { properties = site.AzureAppSettings });
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadAsAsync<ArmWrapper<Dictionary<string, string>>>();
                return new HttpResult<Dictionary<string, string>, string>(result.properties);
            }
            else
            {
                var result = await response.Content.ReadAsStringAsync();
                var parsedResult = JsonConvert.DeserializeObject<JObject>(result);
                var errorMessage = parsedResult["Message"].ToString();
                return string.IsNullOrEmpty(errorMessage)
                    ? new HttpResult<Dictionary<string, string>, string>(null, result)
                    : new HttpResult<Dictionary<string, string>, string>(null, errorMessage);
            }
        }
    }
}