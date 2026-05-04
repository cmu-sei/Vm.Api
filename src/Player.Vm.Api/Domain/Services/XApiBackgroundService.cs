// Copyright 2022 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license, please see LICENSE.md in the project root for license information or contact permission@sei.cmu.edu for full terms.

using System;
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
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly XApiOptions _xApiOptions;
    private readonly ILogger<XApiBackgroundService> _logger;
    private readonly HttpClient _httpClient;
    private DateTime _lastCleanup = DateTime.UtcNow;

    public XApiBackgroundService(
        IServiceScopeFactory scopeFactory,
        XApiOptions xApiOptions,
        ILogger<XApiBackgroundService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _scopeFactory = scopeFactory;
        _xApiOptions = xApiOptions;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_xApiOptions.Username))
        {
            _logger.LogInformation("xAPI not configured - background service will not process statements");
            return;
        }

        _logger.LogInformation("xAPI background service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var queueService = scope.ServiceProvider.GetRequiredService<IXApiQueueService>();

                var statement = await queueService.DequeueAsync(stoppingToken);

                if (statement != null)
                {
                    await ProcessStatementAsync(statement, queueService, stoppingToken);
                }
                else
                {
                    await Task.Delay(5000, stoppingToken);
                }

                if (DateTime.UtcNow - _lastCleanup > TimeSpan.FromHours(24))
                {
                    await queueService.CleanupOldStatementsAsync(TimeSpan.FromDays(7), stoppingToken);
                    _lastCleanup = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in xAPI background service loop");
                await Task.Delay(10000, stoppingToken);
            }
        }

        _logger.LogInformation("xAPI background service stopped");
    }

    private async Task ProcessStatementAsync(Models.XApiQueuedStatementEntity statement, IXApiQueueService queueService, CancellationToken ct)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_xApiOptions.Endpoint}/statements");
            request.Headers.Add("X-Experience-API-Version", "1.0.3");

            var authString = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{_xApiOptions.Username}:{_xApiOptions.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authString);

            request.Content = new StringContent(statement.StatementJson, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                await queueService.MarkCompletedAsync(statement.Id, ct);
                _logger.LogDebug("Successfully sent xAPI statement {StatementId} with verb {Verb}",
                    statement.Id, statement.Verb);
            }
            else
            {
                var error = $"HTTP {response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}";
                await queueService.MarkFailedAsync(statement.Id, error, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send xAPI statement {StatementId}", statement.Id);
            await queueService.MarkFailedAsync(statement.Id, ex.Message, ct);
        }
    }
}
