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
app.MapPost("config/emu/{tenant}", ([FromBody] MethodInfo value, string tenant) => MapMethod(tenant, value));
app.MapPost("config/proxy/{tenant}", ([FromBody] ProxyInfo value, string tenant) => MapMethod(tenant, value));
app.MapDelete("config/{tenant}", (string tenant) => GetCurrentTenant(tenant).Funcs.Clear());
app.Map("/{tenant:regex(^(?!swagger).*$)}/{**path}", async context =>
{
    var path = context.Request.RouteValues["path"] + "";
    var ten = context.Request.RouteValues["tenant"] + "";
    var tenant = GetCurrentTenant(ten);
    if (tenant.Funcs.TryGetValue(path + context.Request.Method.ToUpper(), out object method) || tenant.Funcs.TryGetValue(path, out method) || tenant.Funcs.TryGetValue("", out method)
    || tenant.Funcs.TryGetValue(ten, out method))
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
                context.Request.EnableBuffering();
                //context.Request.Headers.ToList().ForEach(x => targetRequestMessage?.Headers.TryAddWithoutValidation(x.Key, x.Value.ToArray()));
                //targetRequestMessage?.Headers.TryAddWithoutValidation("X-For", x.Value.ToArray())
                var uri = new Uri(proxyInfo.MapUrl);
                targetRequestMessage.RequestUri = new Uri(uri, uri.PathAndQuery+"/"+url[(url.IndexOf(ten) + ten.Length)..].TrimStart('/'));
                
                targetRequestMessage.Content = new StreamContent(context.Request.Body);

                foreach (var header in context.Request.Headers)
                {
                    if (!targetRequestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) && targetRequestMessage.Content != null)
                    {
                        targetRequestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                    }
                }

                targetRequestMessage.Headers.Host = targetRequestMessage.RequestUri.Host;
                targetRequestMessage.Method = new HttpMethod(context.Request.Method);

                var proxy = string.IsNullOrEmpty(proxyInfo.ProxyUrl) ? null : new WebProxy
                {
                    Address = new Uri(proxyInfo.ProxyUrl),
                    //BypassProxyOnLocal = false,
                    
                };
                if (!string.IsNullOrEmpty(proxyInfo.ProxyLogin))
                {
                    proxy.UseDefaultCredentials = string.IsNullOrEmpty(proxyInfo.ProxyLogin);
                    proxy.Credentials = new NetworkCredential(proxyInfo.ProxyLogin, proxyInfo.ProxyPassword);
                }
                using (var responseMessage = await new HttpClient(new HttpClientHandler() { Proxy = proxy,
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                }, true)
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