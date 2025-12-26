using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// 设置日志输出级别为 Warning 以上
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = long.MaxValue;
});

// Configure Kestrel for LAN access and large files
builder.WebHost.UseUrls("http://0.0.0.0:9000").ConfigureKestrel(serverOptions =>
{
    //serverOptions.ListenAnyIP(9000);
    serverOptions.Limits.MaxRequestBodySize = null; // Unlimited upload size
});

builder.Services.AddCors();

var app = builder.Build();

app.UseCors(x => x.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

// Setup FILES directory
var filesRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FILES");
if (!Directory.Exists(filesRoot))
{
    Directory.CreateDirectory(filesRoot);
}

// Serve static files (frontend)
var embeddedProvider = new ManifestEmbeddedFileProvider(Assembly.GetExecutingAssembly(), "wwwroot");

app.UseDefaultFiles(new DefaultFilesOptions
{
    FileProvider = embeddedProvider
});

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = embeddedProvider,
    ServeUnknownFileTypes = true,
    DefaultContentType = "text/html; charset=utf-8"
});

// Helper to safely resolve paths
// 辅助方法：安全地解析路径，防止目录遍历攻击
string GetSafePath(string relativePath)
{
    // Remove leading slashes to ensure Path.Combine works correctly with relative paths
    relativePath = relativePath?.TrimStart('/', '\\') ?? "";
    var fullPath = Path.GetFullPath(Path.Combine(filesRoot, relativePath));
    if (!fullPath.StartsWith(filesRoot, StringComparison.OrdinalIgnoreCase))
    {
        throw new UnauthorizedAccessException("Access denied");
    }
    return fullPath;
}

// API: List files
// API: 获取文件列表
app.MapGet("/api/list", (string? path) =>
{
    try
    {
        var fullPath = GetSafePath(path);
        if (!Directory.Exists(fullPath))
            return Results.NotFound("Directory not found");

        var dirInfo = new DirectoryInfo(fullPath);
        var items = new List<FileItem>();

        foreach (var dir in dirInfo.GetDirectories())
        {
            items.Add(new FileItem(dir.Name, true, null, dir.LastWriteTime));
        }

        foreach (var file in dirInfo.GetFiles())
        {
            items.Add(new FileItem(file.Name, false, file.Length, file.LastWriteTime));
        }

        return Results.Ok(items);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// API: Upload files
// API: 上传文件
app.MapPost("/api/upload", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest();

    var form = await request.ReadFormAsync();
    var path = form["path"].ToString();
    var files = form.Files;

    try
    {
        var targetDir = GetSafePath(path);
        if (!Directory.Exists(targetDir))
            Directory.CreateDirectory(targetDir);

        foreach (var file in files)
        {
            if (file.Length > 0)
            {
                // Sanitize filename (which might be a relative path now)
                // Ensure we treat it as a relative path to targetDir
                var fileName = file.FileName.Replace('\\', '/').TrimStart('/');

                // Basic traversal protection
                if (fileName.Contains(".."))
                    continue;

                var filePath = Path.GetFullPath(Path.Combine(targetDir, fileName));

                // Ensure the resulting path is still under targetDir
                if (!filePath.StartsWith(targetDir))
                    continue;

                var fileDir = Path.GetDirectoryName(filePath);
                if (fileDir != null && !Directory.Exists(fileDir))
                {
                    Directory.CreateDirectory(fileDir);
                }

                using var stream = new FileStream(filePath, FileMode.Create);
                await file.CopyToAsync(stream);
            }
        }
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
}).DisableAntiforgery(); // Disable for simple API usage

// API: Download file or folder
// API: 下载单个文件或文件夹（自动打包为zip）
app.MapGet("/api/download", (string path, HttpContext context) =>
{
    try
    {
        var fullPath = GetSafePath(path);

        if (File.Exists(fullPath))
        {
            return Results.File(fullPath, "application/octet-stream", Path.GetFileName(fullPath));
        }
        else if (Directory.Exists(fullPath))
        {
            // Zip the folder on the fly
            var zipName = Path.GetFileName(fullPath) + ".zip";
            context.Response.ContentType = "application/zip";
            context.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{zipName}\"");

            using var archive = new ZipArchive(context.Response.BodyWriter.AsStream(), ZipArchiveMode.Create);
            // Recursive add files
            // 递归添加文件到压缩包
            void AddToZip(string sourceDir, string entryPrefix)
            {
                foreach (var file in Directory.GetFiles(sourceDir))
                {
                    var entryName = Path.Combine(entryPrefix, Path.GetFileName(file));
                    archive.CreateEntryFromFile(file, entryName);
                }
                foreach (var dir in Directory.GetDirectories(sourceDir))
                {
                    AddToZip(dir, Path.Combine(entryPrefix, Path.GetFileName(dir)));
                }
            }
            AddToZip(fullPath, "");
            return Results.Empty;
        }

        return Results.NotFound();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// API: Download multiple items as zip
// API: 批量下载多个文件/文件夹（打包为zip）
app.MapPost("/api/download-zip", async (DownloadRequest req, HttpContext context) =>
{
    try
    {
        var zipName = "batch_download.zip";
        context.Response.ContentType = "application/zip";
        context.Response.Headers.Append("Content-Disposition", $"attachment; filename=\"{zipName}\"");

        using var archive = new ZipArchive(context.Response.BodyWriter.AsStream(), ZipArchiveMode.Create);

        foreach (var path in req.Paths)
        {
            var fullPath = GetSafePath(path);
            var entryName = Path.GetFileName(fullPath);

            if (File.Exists(fullPath))
            {
                archive.CreateEntryFromFile(fullPath, entryName);
            }
            else if (Directory.Exists(fullPath))
            {
                 // Recursive add
                // 递归添加文件到压缩包
                void AddToZip(string sourceDir, string entryPrefix)
                {
                    foreach (var file in Directory.GetFiles(sourceDir))
                    {
                        var name = Path.Combine(entryPrefix, Path.GetFileName(file));
                        archive.CreateEntryFromFile(file, name);
                    }
                    foreach (var dir in Directory.GetDirectories(sourceDir))
                    {
                        AddToZip(dir, Path.Combine(entryPrefix, Path.GetFileName(dir)));
                    }
                }
                AddToZip(fullPath, entryName);
            }
        }
        return Results.Empty;
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// API: Create Folder
// API: 创建新文件夹
app.MapPost("/api/create-folder", (CreateFolderRequest req) =>
{
    try
    {
        var fullPath = GetSafePath(Path.Combine(req.Path ?? "", req.Name));
        if (Directory.Exists(fullPath))
            return Results.Conflict("Folder already exists");
        Directory.CreateDirectory(fullPath);
        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// API: Delete
// API: 删除文件或文件夹
app.MapDelete("/api/delete", (string path) =>
{
    try
    {
        var fullPath = GetSafePath(path);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
        else if (Directory.Exists(fullPath))
            Directory.Delete(fullPath, true);
        else
            return Results.NotFound();

        return Results.Ok();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});
Console.WriteLine("FileTrans is running on http://localhost:9000");
app.Run();

record FileItem(string Name, bool IsDirectory, long? Size, DateTime LastModified);
record CreateFolderRequest(string Path, string Name);
record DownloadRequest(string[] Paths);

[JsonSerializable(typeof(List<FileItem>))]
[JsonSerializable(typeof(CreateFolderRequest))]
[JsonSerializable(typeof(DownloadRequest))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}
