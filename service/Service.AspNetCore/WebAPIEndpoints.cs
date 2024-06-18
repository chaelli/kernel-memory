// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.Service.AspNetCore.Models;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.KernelMemory.DocumentStorage;
using System.Xml;
using Microsoft.Extensions.Primitives;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Microsoft.KernelMemory.Service.AspNetCore;

public static class WebAPIEndpoints
{
    private static readonly Dictionary<string, IAsyncEnumerable<MemoryAnswer>> s_askStreams = new();

    public static IEndpointRouteBuilder AddKernelMemoryEndpoints(
        this IEndpointRouteBuilder builder,
        string apiPrefix = "/",
        IEndpointFilter? authFilter = null)
    {
        builder.AddPostUploadEndpoint(apiPrefix, authFilter);
        builder.AddGetIndexesEndpoint(apiPrefix, authFilter);
        builder.AddDeleteIndexesEndpoint(apiPrefix, authFilter);
        builder.AddDeleteDocumentsEndpoint(apiPrefix, authFilter);
        builder.AddAskEndpoint(apiPrefix, authFilter);
        builder.AddAskStreamEndpoint(apiPrefix, authFilter);
        builder.AddSearchEndpoint(apiPrefix, authFilter);
        builder.AddUploadStatusEndpoint(apiPrefix, authFilter);
        builder.AddGetDownloadEndpoint(apiPrefix, authFilter);

        // added by viu
        builder.UseAddUrlEndpoint(apiPrefix, authFilter);
        builder.UseAddSitemapEndpoint(apiPrefix, authFilter);

        return builder;
    }

    public static void AddPostUploadEndpoint(
        this IEndpointRouteBuilder builder, string apiPrefix = "/", IEndpointFilter? authFilter = null)
    {
        RouteGroupBuilder group = builder.MapGroup(apiPrefix);

        // File upload endpoint
        var route = group.MapPost(Constants.HttpUploadEndpoint, async Task<IResult> (
                HttpRequest request,
                IKernelMemory service,
                ILogger<KernelMemoryWebAPI> log,
                CancellationToken cancellationToken) =>
            {
                log.LogTrace("New upload HTTP request, content length {0}", request.ContentLength);

                // Note: .NET doesn't yet support binding multipart forms including data and files
                (HttpDocumentUploadRequest input, bool isValid, string errMsg)
                    = await HttpDocumentUploadRequest.BindHttpRequestAsync(request, cancellationToken)
                        .ConfigureAwait(false);

                log.LogTrace("Index '{0}'", input.Index);

                if (!isValid)
                {
                    log.LogError(errMsg);
                    return Results.Problem(detail: errMsg, statusCode: 400);
                }

                try
                {
                    // UploadRequest => Document
                    var documentId = await service.ImportDocumentAsync(input.ToDocumentUploadRequest(), cancellationToken)
                        .ConfigureAwait(false);

                    log.LogTrace("Doc Id '{1}'", documentId);

                    var url = Constants.HttpUploadStatusEndpointWithParams
                        .Replace(Constants.HttpIndexPlaceholder, input.Index, StringComparison.Ordinal)
                        .Replace(Constants.HttpDocumentIdPlaceholder, documentId, StringComparison.Ordinal);
                    return Results.Accepted(url, new UploadAccepted
                    {
                        DocumentId = documentId,
                        Index = input.Index,
                        Message = "Document upload completed, ingestion pipeline started"
                    });
                }
                catch (Exception e)
                {
                    return Results.Problem(title: "Document upload failed", detail: e.Message, statusCode: 503);
                }
            })
            .Produces<UploadAccepted>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }
    }

    public static void AddGetIndexesEndpoint(
        this IEndpointRouteBuilder builder, string apiPrefix = "/", IEndpointFilter? authFilter = null)
    {
        RouteGroupBuilder group = builder.MapGroup(apiPrefix);

        // List of indexes endpoint
        var route = group.MapGet(Constants.HttpIndexesEndpoint,
                async Task<IResult> (
                    IKernelMemory service,
                    ILogger<KernelMemoryWebAPI> log,
                    CancellationToken cancellationToken) =>
                {
                    log.LogTrace("New index list HTTP request");

                    var result = new IndexCollection();
                    IEnumerable<IndexDetails> list = await service.ListIndexesAsync(cancellationToken)
                        .ConfigureAwait(false);

                    foreach (IndexDetails index in list)
                    {
                        result.Results.Add(index);
                    }

                    return Results.Ok(result);
                })
            .Produces<IndexCollection>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }
    }

    public static void AddDeleteIndexesEndpoint(
        this IEndpointRouteBuilder builder, string apiPrefix = "/", IEndpointFilter? authFilter = null)
    {
        RouteGroupBuilder group = builder.MapGroup(apiPrefix);

        // Delete index endpoint
        var route = group.MapDelete(Constants.HttpIndexesEndpoint,
                async Task<IResult> (
                    [FromQuery(Name = Constants.WebServiceIndexField)]
                    string? index,
                    IKernelMemory service,
                    ILogger<KernelMemoryWebAPI> log,
                    CancellationToken cancellationToken) =>
                {
                    log.LogTrace("New delete document HTTP request, index '{0}'", index);
                    await service.DeleteIndexAsync(index: index, cancellationToken)
                        .ConfigureAwait(false);
                    // There's no API to check the index deletion progress, so the URL is empty
                    var url = string.Empty;
                    return Results.Accepted(url, new DeleteAccepted
                    {
                        Index = index ?? string.Empty,
                        Message = "Index deletion request received, pipeline started"
                    });
                })
            .Produces<DeleteAccepted>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }
    }

    public static void AddDeleteDocumentsEndpoint(
        this IEndpointRouteBuilder builder, string apiPrefix = "/", IEndpointFilter? authFilter = null)
    {
        RouteGroupBuilder group = builder.MapGroup(apiPrefix);

        // Delete document endpoint
        var route = group.MapDelete(Constants.HttpDocumentsEndpoint,
                async Task<IResult> (
                    [FromQuery(Name = Constants.WebServiceIndexField)]
                    string? index,
                    [FromQuery(Name = Constants.WebServiceDocumentIdField)]
                    string documentId,
                    IKernelMemory service,
                    ILogger<KernelMemoryWebAPI> log,
                    CancellationToken cancellationToken) =>
                {
                    log.LogTrace("New delete document HTTP request, index '{0}'", index);
                    await service.DeleteDocumentAsync(documentId: documentId, index: index, cancellationToken)
                        .ConfigureAwait(false);
                    var url = Constants.HttpUploadStatusEndpointWithParams
                        .Replace(Constants.HttpIndexPlaceholder, index, StringComparison.Ordinal)
                        .Replace(Constants.HttpDocumentIdPlaceholder, documentId, StringComparison.Ordinal);
                    return Results.Accepted(url, new DeleteAccepted
                    {
                        DocumentId = documentId,
                        Index = index ?? string.Empty,
                        Message = "Document deletion request received, pipeline started"
                    });
                })
            .Produces<DeleteAccepted>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }
    }

    public static void AddAskEndpoint(
        this IEndpointRouteBuilder builder, string apiPrefix = "/", IEndpointFilter? authFilter = null)
    {
        RouteGroupBuilder group = builder.MapGroup(apiPrefix);

        // Ask endpoint
        var route = group.MapPost(Constants.HttpAskEndpoint,
                async Task<IResult> (
                    MemoryQuery query,
                    IKernelMemory service,
                    ILogger<KernelMemoryWebAPI> log,
                    CancellationToken cancellationToken) =>
                {
                    log.LogTrace("New search request, index '{0}', minRelevance {1}", query.Index, query.MinRelevance);
                    MemoryAnswer answer = await service.AskAsync(
                            question: query.Question,
                            index: query.Index,
                            filters: query.Filters,
                            minRelevance: query.MinRelevance,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(answer);
                })
            .Produces<MemoryAnswer>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }
    }

    public static void AddAskStreamEndpoint(
        this IEndpointRouteBuilder builder, string apiPrefix = "/", IEndpointFilter? authFilter = null)
    {
        RouteGroupBuilder group = builder.MapGroup(apiPrefix);

        // Ask streaming endpoint
        var route = group.MapPost(Constants.HttpAskStreamEndpoint, async Task<IResult> (
                HttpContext context,
                MemoryQuery query,
                IKernelMemory service,
                ILogger<KernelMemoryWebAPI> log,
                CancellationToken cancellationToken) =>
            {
                log.LogTrace("New search request, index '{0}', minRelevance {1}", query.Index, query.MinRelevance);

                var askId = Guid.NewGuid().ToString("N").Substring(0, 8);
                s_askStreams.Add(askId, service.AskStreamingAsync(
                    question: query.Question,
                    index: query.Index,
                    filters: query.Filters,
                    minRelevance: query.MinRelevance,
                    cancellationToken: cancellationToken));

                return Results.Ok(new AskStreamResponse { AskId = askId });
            })
            .Produces<AskStreamResponse>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }

        // SSE endpoint
        var sseRoute = group.MapGet($"{Constants.HttpAskStreamEndpoint}/{{askId}}", async Task (
            HttpContext context,
            ILogger<KernelMemoryWebAPI> log,
            CancellationToken cancellationToken,
            string askId) =>
            {
                log.LogTrace("Accessing SSE stream for askId '{0}'", askId);
                context.Response.Headers.Add("Content-Type", "text/event-stream");
                if (s_askStreams.TryGetValue(askId, out IAsyncEnumerable<MemoryAnswer>? stream))
                {
                    s_askStreams.Remove(askId);
                    var jsonOptions = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };
                    var response = context.Response;
                    if (stream != null)
                    {
                        await foreach (var ma in stream)
                        {
                            await response.WriteAsync($"data: {JsonSerializer.Serialize(new { message = ma }, jsonOptions)}\n\n", cancellationToken).ConfigureAwait(false);
                            await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }

                        // Send an end-of-stream event if needed
                        await response.WriteAsync("event: end\ndata: End of stream\n\n", cancellationToken).ConfigureAwait(false);
                        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("Not found", cancellationToken).ConfigureAwait(false);
            });
    }

    public static void AddSearchEndpoint(
        this IEndpointRouteBuilder builder, string apiPrefix = "/", IEndpointFilter? authFilter = null)
    {
        RouteGroupBuilder group = builder.MapGroup(apiPrefix);

        // Search endpoint
        var route = group.MapPost(Constants.HttpSearchEndpoint,
                async Task<IResult> (
                    SearchQuery query,
                    IKernelMemory service,
                    ILogger<KernelMemoryWebAPI> log,
                    CancellationToken cancellationToken) =>
                {
                    log.LogTrace("New search HTTP request, index '{0}', minRelevance {1}", query.Index, query.MinRelevance);
                    SearchResult answer = await service.SearchAsync(
                            query: query.Query,
                            index: query.Index,
                            filters: query.Filters,
                            minRelevance: query.MinRelevance,
                            limit: query.Limit,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    return Results.Ok(answer);
                })
            .Produces<SearchResult>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }
    }

    public static void AddUploadStatusEndpoint(
        this IEndpointRouteBuilder builder, string apiPrefix = "/", IEndpointFilter? authFilter = null)
    {
        RouteGroupBuilder group = builder.MapGroup(apiPrefix);

        // Document status endpoint
        var route = group.MapGet(Constants.HttpUploadStatusEndpoint,
                async Task<IResult> (
                    [FromQuery(Name = Constants.WebServiceIndexField)]
                    string? index,
                    [FromQuery(Name = Constants.WebServiceDocumentIdField)]
                    string documentId,
                    IKernelMemory memoryClient,
                    ILogger<KernelMemoryWebAPI> log,
                    CancellationToken cancellationToken) =>
                {
                    log.LogTrace("New document status HTTP request");
                    if (string.IsNullOrEmpty(documentId))
                    {
                        return Results.Problem(detail: $"'{Constants.WebServiceDocumentIdField}' query parameter is missing or has no value", statusCode: 400);
                    }

                    DataPipelineStatus? pipeline = await memoryClient.GetDocumentStatusAsync(documentId: documentId, index: index, cancellationToken)
                        .ConfigureAwait(false);
                    if (pipeline == null)
                    {
                        return Results.Problem(detail: "Document not found", statusCode: 404);
                    }

                    if (pipeline.Empty)
                    {
                        return Results.Problem(detail: "Empty pipeline", statusCode: 404);
                    }

                    return Results.Ok(pipeline);
                })
            .Produces<DataPipelineStatus>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }
    }

    public static void AddGetDownloadEndpoint(this IEndpointRouteBuilder builder, string apiPrefix = "/", IEndpointFilter? authFilter = null)
    {
        RouteGroupBuilder group = builder.MapGroup(apiPrefix);

        // File download endpoint
        var route = group.MapGet(Constants.HttpDownloadEndpoint, async Task<IResult> (
                [FromQuery(Name = Constants.WebServiceIndexField)]
                string? index,
                [FromQuery(Name = Constants.WebServiceDocumentIdField)]
                string documentId,
                [FromQuery(Name = Constants.WebServiceFilenameField)]
                string filename,
                HttpContext httpContext,
                IKernelMemory service,
                ILogger<KernelMemoryWebAPI> log,
                CancellationToken cancellationToken) =>
            {
                var isValid = !(
                    string.IsNullOrWhiteSpace(documentId) ||
                    string.IsNullOrWhiteSpace(filename));
                var errMsg = "Missing required parameter";

                log.LogTrace("New download file HTTP request, index {0}, documentId {1}, fileName {3}", index, documentId, filename);

                if (!isValid)
                {
                    log.LogError(errMsg);
                    return Results.Problem(detail: errMsg, statusCode: 400);
                }

                try
                {
                    // DownloadRequest => Document
                    var file = await service.ExportFileAsync(
                            documentId: documentId,
                            fileName: filename,
                            index: index,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    if (file == null)
                    {
                        log.LogWarning("Returned file is NULL, file not found");
                        return Results.Problem(title: "File not found", statusCode: 404);
                    }

                    log.LogTrace("Downloading file '{0}', size '{1}', type '{2}'", filename, file.FileSize, file.FileType);
                    Stream resultingFileStream = await file.GetStreamAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                    var response = Results.Stream(
                        resultingFileStream,
                        contentType: file.FileType,
                        fileDownloadName: filename,
                        lastModified: file.LastWrite,
                        enableRangeProcessing: true);

                    // Add content length header if missing
                    if (response is FileStreamHttpResult { FileLength: null or 0 })
                    {
                        httpContext.Response.Headers.ContentLength = file.FileSize;
                    }

                    return response;
                }
                catch (DocumentStorageFileNotFoundException e)
                {
                    return Results.Problem(title: "File not found", detail: e.Message, statusCode: 404);
                }
                catch (Exception e)
                {
                    return Results.Problem(title: "File download failed", detail: e.Message, statusCode: 503);
                }
            })
            .Produces<StreamableFileContent>(StatusCodes.Status200OK)
            .Produces<ProblemDetails>(StatusCodes.Status404NotFound)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }
    }

    public static void UseAddUrlEndpoint(this IEndpointRouteBuilder builder, string apiPrefix = "/", IEndpointFilter? authFilter = null)
    {
        RouteGroupBuilder group = builder.MapGroup(apiPrefix);
        // File upload endpoint
        var route = group.MapPost("/addurl", async Task<IResult> (
                HttpRequest request,
                IKernelMemory service,
                ILogger<KernelMemoryWebAPI> log,
                CancellationToken cancellationToken) =>
            {
                log.LogTrace("New add url HTTP request, content length {0}", request.ContentLength);
                // Note: .NET doesn't yet support binding multipart forms including data and files
                (HttpAddUrlRequest input, bool isValid, string errMsg)
                    = await HttpAddUrlRequest.BindHttpRequestAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                log.LogTrace("Index '{0}'", input.Index);
                if (!isValid)
                {
                    log.LogError(errMsg);
                    return Results.Problem(detail: errMsg, statusCode: 400);
                }
                try
                {
                    //await s_memory.ImportWebPageAsync("https://raw.githubusercontent.com/microsoft/kernel-memory/main/README.md", documentId: "webPage1");
                    // UploadRequest => Document
                    var documentId = await service.ImportWebPageAsync(
                        url: input.Url.ToString(),
                        documentId: input.DocumentId,
                        tags: input.Tags,
                        index: input.Index,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                    log.LogTrace("Doc Id '{1}'", documentId);
                    var url = Constants.HttpUploadStatusEndpointWithParams
                        .Replace(Constants.HttpIndexPlaceholder, input.Index, StringComparison.Ordinal)
                        .Replace(Constants.HttpDocumentIdPlaceholder, documentId, StringComparison.Ordinal);
                    return Results.Accepted(url, new UploadAccepted
                    {
                        DocumentId = documentId,
                        Index = input.Index,
                        Message = "Document upload completed, ingestion pipeline started"
                    });
                }
                catch (Exception e)
                {
                    return Results.Problem(title: "Document upload failed", detail: e.Message, statusCode: 503);
                }
            })
            .Produces<UploadAccepted>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }
    }

    public static void UseAddSitemapEndpoint(this IEndpointRouteBuilder builder, string apiPrefix = "/", IEndpointFilter? authFilter = null)
    {
        RouteGroupBuilder group = builder.MapGroup(apiPrefix);

        // File upload endpoint
        var route = group.MapPost("/addsitemap", async Task<IResult> (
                HttpRequest request,
                IKernelMemory service,
                ILogger<KernelMemoryWebAPI> log,
                //IHttpClientFactory clientFactory,
                CancellationToken cancellationToken) =>
            {
                log.LogTrace("New add sitemap HTTP request, content length {0}", request.ContentLength);
                // Note: .NET doesn't yet support binding multipart forms including data and files
                (HttpAddUrlRequest input, bool isValid, string errMsg)
                    = await HttpAddUrlRequest.BindHttpRequestAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                log.LogTrace("Index '{0}'", input.Index);
                if (!isValid)
                {
                    log.LogError(errMsg);
                    return Results.Problem(detail: errMsg, statusCode: 400);
                }
                try
                {
                    // get http client and use it to retrieve sitemap xml from "url"
                    /*var client = clientFactory.CreateClient();
                    var response = await client.GetAsync(input.Url, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    var sitemap = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);*/
                    var docIds = new List<string>();
                    // Load the sitemap XML from the URL
                    var doc = new XmlDocument();
                    doc.Load(input.Url.ToString());
                    // Namespace manager for handling namespaces in the XML
                    var manager = new XmlNamespaceManager(doc.NameTable);
                    manager.AddNamespace("ns", "http://www.sitemaps.org/schemas/sitemap/0.9");
                    // Select all <url> nodes and iterate over them
                    var nodes = doc.SelectNodes("//ns:url/ns:loc", manager);
                    foreach (XmlNode node in nodes)
                    {
                        var documentId = await service.ImportWebPageAsync(
                            documentId: WebAPIEndpoints.GetDocumentIdFromUrl(node.InnerText),
                            url: node.InnerText,
                            tags: input.Tags,
                            index: input.Index,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        Console.WriteLine("added " + node.InnerText);
                        docIds.Add(documentId);
                        // wait for 1 second to avoid rate limiting
                        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                    }
                    log.LogTrace("Doc Ids '{1}'", string.Join(',', docIds));
                    var url = Constants.HttpUploadStatusEndpointWithParams
                        .Replace(Constants.HttpIndexPlaceholder, input.Index, StringComparison.Ordinal)
                        .Replace(Constants.HttpDocumentIdPlaceholder, string.Join(',', docIds), StringComparison.Ordinal);
                    return Results.Accepted(url, new UploadAccepted
                    {
                        DocumentId = string.Join(',', docIds),
                        Index = input.Index,
                        Message = "Document upload completed, ingestion pipeline started"
                    });
                }
                catch (Exception e)
                {
                    return Results.Problem(title: "Document upload failed", detail: e.Message, statusCode: 503);
                }
            })
            .Produces<UploadAccepted>(StatusCodes.Status202Accepted)
            .Produces<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Produces<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Produces<ProblemDetails>(StatusCodes.Status403Forbidden)
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);

        if (authFilter != null) { route.AddEndpointFilter(authFilter); }
    }



    private static string? GetDocumentIdFromUrl(string url)
    {
        var id = url?.Replace("https://", "", StringComparison.OrdinalIgnoreCase).Replace("http://", "", StringComparison.OrdinalIgnoreCase).Replace("/", "_", StringComparison.OrdinalIgnoreCase).Replace(".", "_", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
        return id == null ? null : Regex.Replace(id, @"[^A-Za-z0-9._-]", "_");
    }

#pragma warning disable CA1812 // used by logger, can't be static
    // Class used to tag log entries and allow log filtering
    private sealed class KernelMemoryWebAPI;
#pragma warning restore CA1812

    public class HttpAddUrlRequest
    {
        public string Index { get; set; } = string.Empty;
        public string DocumentId { get; set; } = string.Empty;
        public TagCollection Tags { get; set; } = new();
        public List<string> Steps { get; set; } = new();
        public Uri Url { get; set; } = null!;

        /* Resources:
         * https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-7.0
         * https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-7.0#upload-large-files-with-streaming
         * https://stackoverflow.com/questions/71499435/how-do-i-do-file-upload-using-asp-net-core-6-minimal-api
         * https://stackoverflow.com/questions/57033535/multipartformdatacontent-add-stringcontent-is-adding-carraige-return-linefeed-to
         */
        public static async Task<(HttpAddUrlRequest model, bool isValid, string errMsg)> BindHttpRequestAsync(
            HttpRequest httpRequest, CancellationToken cancellationToken = default)
        {
            var result = new HttpAddUrlRequest();

            // Read form
            IFormCollection form = await httpRequest.ReadFormAsync(cancellationToken).ConfigureAwait(false);

            // Only one index can be defined
            if (form.TryGetValue(Constants.WebServiceIndexField, out StringValues indexes) && indexes.Count > 1)
            {
                return (result, false, $"Invalid index name, '{Constants.WebServiceIndexField}', multiple values provided");
            }

            // Only one document ID can be defined
            if (form.TryGetValue(Constants.WebServiceDocumentIdField, out StringValues documentIds) && documentIds.Count > 1)
            {
                return (result, false, $"Invalid document ID, '{Constants.WebServiceDocumentIdField}' must be a single value, not a list");
            }

            // Document Id is optional, e.g. used if the client wants to retry the same upload, otherwise a random/unique one is generated
            string? documentId = documentIds.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(documentId))
            {
                documentId = DateTimeOffset.Now.ToString("yyyyMMdd.HHmmss.", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N");
            }

            // exactly one url needs to be provided
            if (!form.TryGetValue("url", out StringValues urlString) || urlString.Count != 1 || !Uri.TryCreate(urlString.FirstOrDefault(), UriKind.Absolute, out Uri url))
            {
                return (result, false, $"Invalid URL, 'url' must be a single valid URL");
            }

            // Optional document tags. Tags are passed in as "key:value", where a key can have multiple values. See TagCollection.
            if (form.TryGetValue(Constants.WebServiceTagsField, out StringValues tags))
            {
                foreach (string? tag in tags)
                {
                    if (tag == null) { continue; }

                    var keyValue = tag.Trim().Split(Constants.ReservedEqualsChar, 2);
                    string key = keyValue[0].Trim();
                    if (string.IsNullOrWhiteSpace(key)) { continue; }

                    ValidateTagName(key);

                    string? value = keyValue.Length == 1 ? null : keyValue[1].Trim();
                    if (string.IsNullOrWhiteSpace(value)) { value = null; }

                    result.Tags.Add(key, value);
                }
            }

            // Optional pipeline steps. The user can pass a custom list or leave it to the system to use the default.
            if (form.TryGetValue(Constants.WebServiceStepsField, out StringValues steps))
            {
                foreach (string? step in steps)
                {
                    if (string.IsNullOrWhiteSpace(step)) { continue; }

                    // Allow step names to be separated by space, comma, semicolon
                    var list = step.Replace(' ', ';').Replace(',', ';').Split(';');
                    result.Steps.AddRange(from s in list where !string.IsNullOrWhiteSpace(s) select s.Trim());
                }
            }

            result.Index = indexes.FirstOrDefault()?.Trim() ?? string.Empty;
            result.DocumentId = documentId;
            result.Url = url;

            return (result, true, string.Empty);
        }

        private static void ValidateTagName(string tagName)
        {
            if (tagName.StartsWith(Constants.ReservedTagsPrefix, StringComparison.Ordinal))
            {
                throw new KernelMemoryException(
                    $"The tag name prefix '{Constants.ReservedTagsPrefix}' is reserved for internal use.");
            }

            if (tagName is Constants.ReservedDocumentIdTag
                or Constants.ReservedFileIdTag
                or Constants.ReservedFilePartitionTag
                or Constants.ReservedFileTypeTag
                or Constants.ReservedSyntheticTypeTag)
            {
                throw new KernelMemoryException($"The tag name '{tagName}' is reserved for internal use.");
            }
        }
    }
}
