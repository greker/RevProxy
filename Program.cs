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
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder();
builder.Services.AddSwaggerGen(swagger => swagger.SwaggerDoc("v1", new OpenApiInfo { Title = "Emul API" }));
builder.Services.AddControllers().AddNewtonsoftJson();
var app = builder.Build();
app.UseRouting().UseEndpoints(endpoints => endpoints.MapControllers());
app.UseSwagger().UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "My API V1"));

app.Map("/{tenant:regex(^(?!.*swagger).*$)}/{*path:regex(^(?!.*config).*$)}", async context =>
{
    var path = context.Request.RouteValues["path"] + "";
    var tenant = ConfigController.GetCurrentTenant(context.Request.RouteValues["tenant"] + "");
    if (tenant.Funcs.TryGetValue(path + context.Request.Method.ToUpper(), out RecInfo method) || tenant.Funcs.TryGetValue(path, out method))
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

public record TenantInfo(ConcurrentDictionary<string, RecInfo> Funcs);
public record RecInfo([property: Newtonsoft.Json.JsonIgnore] string Id, string Name);
public record MethodInfo(string Name, string Method, int Status, string Response, Dictionary<string, string> Headers) : RecInfo(Name + Method?.ToUpper(), Name);
public record ProxyInfo(string Name, string MapUrl, string ProxyUrl, string ProxyLogin, string ProxyPassword) : RecInfo(Name, Name);