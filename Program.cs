using System;
using System.IO;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using System.Text.Json;

ConcurrentDictionary<string, TenantInfo> info = new();

var builder = WebApplication.CreateBuilder();
builder.Services.AddEndpointsApiExplorer().AddSwaggerGen();
var app = builder.Build();
app.UseSwagger().UseSwaggerUI();

app.MapGet("{tenant}/config",(string tenant) => Results.Json(GetCurrentTenant(tenant).Funcs.Values, new JsonSerializerOptions
{
    WriteIndented = true,
}));
app.MapPost("{tenant}/config/emu", ([FromBody] MethodInfo value, string tenant) => MapMethod(tenant, value));
app.MapPost("{tenant}/config/proxy", ([FromBody] ProxyInfo value, string tenant) => MapMethod(tenant, value));
app.MapDelete("{tenant}/config", (string tenant) => GetCurrentTenant(tenant).Funcs.Clear());
app.Map("/{tenant}/{*path}", async context =>
{
    var path = context.Request.RouteValues["path"] + "";
    var tenant = GetCurrentTenant(context.Request.RouteValues["tenant"] + "");
    if (tenant.Funcs.TryGetValue(path + context.Request.Method.ToUpper(), out object method) || tenant.Funcs.TryGetValue(path, out method))
    {
        switch (method)
        {
            case MethodInfo methodInfo:
                context.Response.StatusCode = methodInfo.Status;
                methodInfo.Headers?.ToList().ForEach(x => context.Response.Headers[x.Key] = x.Value);
                await context.Response.WriteAsync(methodInfo.Response);
                break;
            case ProxyInfo proxyInfo:
                var url = context.Request.GetDisplayUrl();
                var targetRequestMessage = new HttpRequestMessage();
                context.Request.Headers.ToList().ForEach(x => targetRequestMessage?.Headers.TryAddWithoutValidation(x.Key, x.Value.ToArray()));
                targetRequestMessage.RequestUri = new Uri(new Uri(proxyInfo.MapUrl), url[url.IndexOf(path)..]);
                targetRequestMessage.Headers.Host = targetRequestMessage.RequestUri.Host;
                targetRequestMessage.Method = new HttpMethod(context.Request.Method);
                targetRequestMessage.Content = new StreamContent(context.Request.Body);
                var proxy = string.IsNullOrEmpty(proxyInfo.ProxyUrl) ? null : new WebProxy
                {
                    Address = new Uri(proxyInfo.ProxyUrl),
                    BypassProxyOnLocal = false,
                    UseDefaultCredentials = string.IsNullOrEmpty(proxyInfo.ProxyLogin),
                    Credentials = new NetworkCredential(proxyInfo.ProxyLogin, proxyInfo.ProxyPassword)
                };
                using (var responseMessage = await new HttpClient(new HttpClientHandler() { Proxy = proxy }, true)
                        .SendAsync(targetRequestMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted))
                {
                    context.Response.StatusCode = (int)responseMessage.StatusCode;
                    responseMessage.Headers.ToList().ForEach(x => context.Response.Headers[x.Key] = x.Value.ToArray());
                    responseMessage.Content.Headers.ToList().ForEach(x => context.Response.Headers[x.Key] = x.Value.ToArray());
                    context.Response.Headers.Remove("transfer-encoding");
                    await responseMessage.Content.CopyToAsync(context.Response.Body);
                }
                break;
        }
    }
});

await app.RunAsync();

TenantInfo GetCurrentTenant(string tenant) => info.GetOrAdd(tenant, (t) => new TenantInfo(new ConcurrentDictionary<string, object>()));

void MapMethod(string tenant, RecInfo res)
{
    if (res.Name is not null && res.Name != "config" && !res.Name.StartsWith("config/"))
        GetCurrentTenant(tenant).Funcs.AddOrUpdate(res.Id, res, (s, d) => res);
}

public record TenantInfo(ConcurrentDictionary<string, object> Funcs);
public record RecInfo([property: JsonIgnore] string Id, string Name);
public record MethodInfo(string Name, string Method, int Status, string Response, Dictionary<string, string> Headers) : RecInfo(Name + Method?.ToUpper(), Name);
public record ProxyInfo(string Name, string MapUrl, string ProxyUrl, string ProxyLogin, string ProxyPassword) : RecInfo(Name, Name);