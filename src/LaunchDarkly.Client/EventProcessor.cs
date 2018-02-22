﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace LaunchDarkly.Client
{
    public sealed class EventProcessor : IDisposable, IStoreEvents
    {
        private static readonly ILogger Logger = LdLogger.CreateLogger<EventProcessor>();

        private readonly Configuration _config;
        private readonly BlockingCollection<Event> _queue;
        private readonly Timer _timer;
        private volatile HttpClient _httpClient;
        private readonly Uri _uri;
        private volatile bool _shutdown;

        public EventProcessor(Configuration config)
        {
            _config = config;
            _httpClient = config.HttpClient();
            _queue = new BlockingCollection<Event>(_config.EventQueueCapacity);
            _timer = new Timer(SubmitEvents, null, _config.EventQueueFrequency,
                _config.EventQueueFrequency);
            _uri = new Uri(_config.EventsUri.AbsoluteUri + "bulk");
        }

        private void SubmitEvents(object StateInfo)
        {
            ((IStoreEvents) this).Flush();
        }

        void IStoreEvents.Add(Event eventToLog)
        {
            if (!_queue.TryAdd(eventToLog))
                Logger.LogWarning("Exceeded event queue capacity. Increase capacity to avoid dropping events.");
        }

        void IDisposable.Dispose()
        {
            ((IStoreEvents) this).Flush();
            _queue.CompleteAdding();
            _timer.Dispose();
            _queue.Dispose();
        }

        void IStoreEvents.Flush()
        {
            if (!_shutdown)
            {
                Event e;
                List<Event> events = new List<Event>();
                while (_queue.TryTake(out e))
                {
                    events.Add(e);
                }

                if (events.Any())
                {
                    Task.Run(() => BulkSubmitAsync(events)).GetAwaiter().GetResult();
                }
            }
        }

        private async Task BulkSubmitAsync(IList<Event> events)
        {
            var cts = new CancellationTokenSource(_config.HttpClientTimeout);
            var jsonEvents = "";
            try
            {
                jsonEvents = JsonConvert.SerializeObject(events.ToList(), Formatting.None);

                Logger.LogDebug("Submitting {0} events to {1} with json: {2}",
                    events.Count,
                    _uri.AbsoluteUri,
                    jsonEvents);

                await SendEventsAsync(jsonEvents, cts);
            }
            catch (Exception e)
            {
                Logger.LogDebug(e,
                    "Error sending events: {0} waiting 1 second before retrying.",
                    Util.ExceptionMessage(e));

                Task.Delay(TimeSpan.FromSeconds(1)).Wait();
                cts = new CancellationTokenSource(_config.HttpClientTimeout);
                try
                {
                    Logger.LogDebug("Submitting {0} events to {1} with json: {2}",
                        events.Count,
                        _uri.AbsoluteUri,
                        jsonEvents);
                    await SendEventsAsync(jsonEvents, cts);
                }
                catch (TaskCanceledException tce)
                {
                    if (tce.CancellationToken == cts.Token)
                    {
                        //Indicates the task was cancelled by something other than a request timeout
                        Logger.LogError(tce,
                            "Error Submitting Events using uri: '{0}' '{1}'",
                            _uri.AbsoluteUri,
                            Util.ExceptionMessage(tce));
                    }
                    else
                    {
                        //Otherwise this was a request timeout.
                        Logger.LogError(tce,
                            "Timed out trying to send {0} events after {1}",
                            events.Count,
                            _config.HttpClientTimeout);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex,
                        "Error Submitting Events using uri: '{0}' '{1}'",
                        _uri.AbsoluteUri,
                         Util.ExceptionMessage(ex));
                }
            }
        }


        private async Task SendEventsAsync(String jsonEvents, CancellationTokenSource cts)
        {
            using (var stringContent = new StringContent(jsonEvents, Encoding.UTF8, "application/json"))
            using (var response = await _httpClient.PostAsync(_uri, stringContent).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogError("Error Submitting Events using uri: '{0}'; Status: '{1}'",
                        _uri.AbsoluteUri,
                        response.StatusCode);
                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        Logger.LogError("Received 401 error, no further events will be posted since SDK key is invalid");
                        _shutdown = true;
                        ((IDisposable)this).Dispose();
                    }
                }
                else
                {
                    Logger.LogDebug("Got {0} when sending events.",
                        response.StatusCode);
                }
            }
        }
    }
}