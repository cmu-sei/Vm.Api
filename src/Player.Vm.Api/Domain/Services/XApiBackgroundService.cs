// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Player.Vm.Api.Infrastructure.Options;

namespace Player.Vm.Api.Domain.Services;

public class XApiBackgroundService : BackgroundService
{
    private const int BatchSize = 10;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<XApiBackgroundService> _logger;
    private DateTime _lastCleanup = DateTime.MinValue;

    public XApiBackgroundService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<XApiBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var options = scope.ServiceProvider.GetRequiredService<XApiOptions>();

                if (!XApiService.IsConfigured(options))
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                var queue = scope.ServiceProvider.GetRequiredService<IXApiQueueService>();
                await queue.ResetStaleProcessingAsync(DateTime.UtcNow.AddMinutes(-10), stoppingToken);

                var statements = await queue.DequeueAsync(BatchSize, stoppingToken);
                foreach (var statement in statements)
                {
                    await SendAsync(statement.Id, statement.StatementJson, options, queue, stoppingToken);
                }

                if (DateTime.UtcNow - _lastCleanup > TimeSpan.FromHours(24))
                {
                    await queue.CleanupAsync(DateTime.UtcNow.AddDays(-options.RetentionDays), stoppingToken);
                    _lastCleanup = DateTime.UtcNow;
                }

                if (statements.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.ProcessingDelaySeconds), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error while processing the xAPI statement queue.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task SendAsync(
        Guid statementId,
        string statementJson,
        XApiOptions options,
        IXApiQueueService queue,
        CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{options.Endpoint.TrimEnd('/')}/statements");
            request.Headers.Add("X-Experience-API-Version", "1.0.3");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}")));
            request.Content = new StringContent(statementJson, Encoding.UTF8, "application/json");

            using var response = await _httpClientFactory.CreateClient("xapi").SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                await queue.MarkCompletedAsync(statementId, ct);
                return;
            }

            var responseText = await response.Content.ReadAsStringAsync(ct);
            var error = $"HTTP {(int)response.StatusCode}: {responseText}";
            await queue.MarkFailedAsync(statementId, error, IsTransient(response.StatusCode), ct);
        }
        catch (HttpRequestException ex)
        {
            await queue.MarkFailedAsync(statementId, ex.Message, true, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            await queue.MarkFailedAsync(statementId, "The xAPI request timed out.", true, ct);
        }
        catch (Exception ex)
        {
            await queue.MarkFailedAsync(statementId, ex.Message, false, ct);
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;
}
