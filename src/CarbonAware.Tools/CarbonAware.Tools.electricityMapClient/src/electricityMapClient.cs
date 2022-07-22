﻿using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CarbonAware.Tools.electricityMapClient.Model;
using System.Web;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Security.Authentication;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Net;
using CarbonAware.Tools.electricityMapClient.Configuration;
using CarbonAware.Tools.electricityMapClient.Constants;
using System.Globalization;

namespace CarbonAware.Tools.electricityMapClient;

public class electricityMapClient : IelectricityMapClient
{
    private static readonly JsonSerializerOptions options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    private static readonly HttpStatusCode[] RetriableStatusCodes = new HttpStatusCode[]
    {
        HttpStatusCode.Unauthorized, 
        HttpStatusCode.Forbidden
    };

    private HttpClient client;

    private IOptionsMonitor<electricityMapClientConfiguration> ConfigurationMonitor { get; }

    private electricityMapClientConfiguration Configuration => this.ConfigurationMonitor.CurrentValue;

    private static readonly ActivitySource Activity = new ActivitySource(nameof(electricityMapClient));

    private ILogger<electricityMapClient> Log { get; }

    public electricityMapClient(IHttpClientFactory factory, IOptionsMonitor<electricityMapClientConfiguration> configurationMonitor, ILogger<electricityMapClient> log)
    {
        this.client = factory.CreateClient(IelectricityMapClient.NamedClient);
        this.ConfigurationMonitor = configurationMonitor;
        this.Log = log;
        this.client.BaseAddress = new Uri(this.Configuration.BaseUrl);
        this.client.DefaultRequestHeaders.Accept.Clear();
        this.client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
    }

    /// <inheritdoc/>
    public async Task<Forecast?> GetCurrentForecastAsync(string countryCodeAbbreviation)
    {

        Log.LogInformation("Requesting current forecast from zone {countryCode}", countryCodeAbbreviation);

        var parameters = new Dictionary<string, string>()
        {
            { QueryStrings.countryCodeAbbreviation, countryCodeAbbreviation }
        };

        var tags = new Dictionary<string, string>()
        {
            { QueryStrings.countryCodeAbbreviation, countryCodeAbbreviation }
        };

        var result = await this.MakeRequestAsync(parameters, tags);

        var forecast = JsonSerializer.Deserialize<Forecast?>(result, options) ?? throw new electricityMapClientException($"Error getting forecast for  {countryCodeAbbreviation}");

        return forecast;
    }

    /// <inheritdoc/>
    public Task<Forecast?> GetCurrentForecastAsync(Zone zone)
    {
        return this.GetCurrentForecastAsync(zone.countryCode);
    }

    private async Task<HttpResponseMessage> GetAsyncWithAuthRetry(string uriPath)
    {
        var response = await this.client.GetAsync(uriPath);

        if (RetriableStatusCodes.Contains(response.StatusCode))
        {
            Log.LogDebug("Failed to get url {url} with status code {statusCode}.  Attempting to log in again.", uriPath, response.StatusCode);
            response = await this.client.GetAsync(uriPath);
        }

        if (!response.IsSuccessStatusCode)
        {
            Log.LogError("Error getting data from electricityMap.  StatusCode: {statusCode}. Response: {response}", response.StatusCode, response);

            throw new electricityMapClientHttpException($"Error getting data from electricityMap: {response.StatusCode}", response);
        }

        return response;
    }

    private async Task<string> GetAsyncStringWithAuthRetry(string uriPath)
    {
        var response = await this.GetAsyncWithAuthRetry(uriPath);
        var data = await response.Content.ReadAsStringAsync();
        return data ?? string.Empty;
    }

    private async Task<Stream> GetAsyncStreamWithAuthRetry(string uriPath)
    {
        var response = await this.GetAsyncWithAuthRetry(uriPath);
        return await response.Content.ReadAsStreamAsync();
    }

    internal void SetBearerAuthenticationHeader(string token)
    {
        this.client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(AuthenticationHeaderTypes.Token, token);
    }


    // Overload method for electricity Map Personal 
    private async Task<string> MakeRequestAsync(Dictionary<string, string> parameters, Dictionary<string, string>? tags = null)
    {
        using (var activity = Activity.StartActivity())
        {
            var url = BuildUrlWithQueryString(parameters);

            Log.LogInformation("Requesting data using url {url}", url);

            if (tags != null)
            {
                foreach (var kvp in tags)
                {
                    activity?.AddTag(kvp.Key, kvp.Value);
                }
            }

            var result = await this.GetAsyncStringWithAuthRetry(url);

            Log.LogDebug("For query {url}, received data {result}", url, result);

            return result;
        }
    }

    private string BuildUrlWithQueryString(IDictionary<string, string> queryStringParams)
    {
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Attempting to build a url using query string parameters {parameters}", string.Join(";", queryStringParams.Select(k => $"\"{k.Key}\":\"{k.Value}\"")));
        }

        // this will get a specialized namevalue collection for formatting query strings.
        var query = HttpUtility.ParseQueryString(string.Empty);

        foreach (var kvp in queryStringParams)
        {
            query[kvp.Key] = kvp.Value;
        }

        var result = $"?{query}";

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Built url {result} from query string parameters {parameters}", result, string.Join(";", queryStringParams.Select(k => $"\"{k.Key}\":\"{k.Value}\"")));
        }

        return result;
    }

    private async Task<string> MakeRequestAsync(string path, Dictionary<string, string> parameters, Dictionary<string, string>? tags = null)
    {
        using (var activity = Activity.StartActivity())
        {
            var url = BuildUrlWithQueryString(path, parameters);

            Log.LogInformation("Requesting data using url {url}", url);

            if (tags != null)
            {
                foreach (var kvp in tags)
                {
                    activity?.AddTag(kvp.Key, kvp.Value);
                }
            }

            var result = await this.GetAsyncStringWithAuthRetry(url);

            Log.LogDebug("For query {url}, received data {result}", url, result);

            return result;
        }
    }

    private string BuildUrlWithQueryString(string url, IDictionary<string, string> queryStringParams)
    {
        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Attempting to build a url using url {url} and query string parameters {parameters}", url, string.Join(";", queryStringParams.Select(k => $"\"{k.Key}\":\"{k.Value}\"")));
        }

        // this will get a specialized namevalue collection for formatting query strings.
        var query = HttpUtility.ParseQueryString(string.Empty);

        foreach (var kvp in queryStringParams)
        {
            query[kvp.Key] = kvp.Value;
        }

        var result = $"{url}?{query}";

        if (Log.IsEnabled(LogLevel.Debug))
        {
            Log.LogDebug("Built url {result} from url {url} and query string parameters {parameters}", result, url, string.Join(";", queryStringParams.Select(k => $"\"{k.Key}\":\"{k.Value}\"")));
        }

        return result;
    }
}
