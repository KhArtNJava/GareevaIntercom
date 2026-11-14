using IntercomPlayer.Components;
using IntercomPlayer.Services;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<IntercomOptions>(builder.Configuration.GetSection(IntercomOptions.Section));
builder.Services.AddSingleton<StreamStatus>();
builder.Services.AddSingleton<StreamRelayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StreamRelayService>());

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// HLS-сегменты, которые пишет ffmpeg: кеширование отключено, плейлист обновляется каждую секунду.
var hlsDir = app.Services.GetRequiredService<StreamRelayService>().HlsPath;
Directory.CreateDirectory(hlsDir);
var mime = new FileExtensionContentTypeProvider();
mime.Mappings[".m3u8"] = "application/vnd.apple.mpegurl";
mime.Mappings[".ts"] = "video/mp2t";
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(hlsDir),
    RequestPath = "/hls",
    ContentTypeProvider = mime,
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-store",
});

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
