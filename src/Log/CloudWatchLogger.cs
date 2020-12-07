﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Amazon;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using PipServices3.Aws.Connect;
using PipServices3.Commons.Config;
using PipServices3.Commons.Convert;
using PipServices3.Commons.Data;
using PipServices3.Commons.Errors;
using PipServices3.Commons.Refer;
using PipServices3.Commons.Run;
using PipServices3.Components.Auth;
using PipServices3.Components.Connect;
using PipServices3.Components.Log;

namespace PipServices3.Aws.Log
{
    /// <summary>
    /// Logger that writes log messages to AWS Cloud Watch Log.
    /// 
    /// ### Configuration parameters ###
    /// 
    /// - stream:                        (optional) Cloud Watch Log stream(default: context name)
    /// - group:                         (optional) Cloud Watch Log group(default: context instance ID or hostname)
    /// 
    /// connections:
    /// - discovery_key:               (optional) a key to retrieve the connection from <a href="https://pip-services3-dotnet.github.io/pip-services3-components-dotnet/interface_pip_services_1_1_components_1_1_connect_1_1_i_discovery.html">IDiscovery</a>
    /// - region:                      (optional) AWS region
    /// 
    /// credentials:
    /// - store_key:                   (optional) a key to retrieve the credentials from <a href="https://pip-services3-dotnet.github.io/pip-services3-components-dotnet/interface_pip_services_1_1_components_1_1_auth_1_1_i_credential_store.html">ICredentialStore</a>
    /// - access_id:                   AWS access/client id
    /// - access_key:                  AWS access/client id
    /// 
    /// options:
    /// - interval:        interval in milliseconds to save current counters measurements(default: 5 mins)
    /// - reset_timeout:   timeout in milliseconds to reset the counters. 0 disables the reset(default: 0)
    /// 
    /// ### References ###
    /// 
    /// - *:context-info:*:*:1.0      (optional) <a href="https://pip-services3-dotnet.github.io/pip-services3-components-dotnet/class_pip_services_1_1_components_1_1_info_1_1_context_info.html">ContextInfo</a> to detect the context id and specify counters source
    /// - *:discovery:*:*:1.0         (optional) <a href="https://pip-services3-dotnet.github.io/pip-services3-components-dotnet/interface_pip_services_1_1_components_1_1_connect_1_1_i_discovery.html">IDiscovery</a> services to resolve connections
    /// - *:credential-store:*:*:1.0  (optional) Credential stores to resolve credentials
    /// </summary>
    /// <example>
    /// <code>
    /// var logger = new Logger();
    /// logger.Configure(ConfigParams.FromTuples(
    /// "stream", "mystream",
    /// "group", "mygroup",
    /// "connection.region", "us-east-1",
    /// "connection.access_id", "XXXXXXXXXXX",
    /// "connection.access_key", "XXXXXXXXXXX"     ));
    /// 
    /// logger.SetReferences(References.FromTuples(
    /// new Descriptor("pip-services3", "logger", "console", "default", "1.0"), 
    /// new ConsoleLogger() ));
    /// 
    /// logger.Open("123");
    /// 
    /// logger.SetLevel(LogLevel.debug);
    /// 
    /// logger.Error("123", ex, "Error occured: %s", ex.message);
    /// logger.Debug("123", "Everything is OK.");
    /// </code>
    /// </example>
    /// See <see cref="Counter"/>, <see cref="CachedCounters"/>, <see cref="CompositeCounters"/>
    public class CloudWatchLogger : CachedLogger, IReferenceable, IOpenable
    {
        private FixedRateTimer _timer;
        private AwsConnectionResolver _connectionResolver = new AwsConnectionResolver();
        private AmazonCloudWatchLogsClient _client;
        private string _group = "undefined";
        private string _stream = null;
        private string _lastToken = null;

        /// <summary>
        /// Creates a new instance of this logger.
        /// </summary>
        public CloudWatchLogger()
        { }

        /// <summary>
        /// Configures component by passing configuration parameters.
        /// </summary>
        /// <param name="config">configuration parameters to be set.</param>
        public override void Configure(ConfigParams config)
        {
            base.Configure(config);
            _connectionResolver.Configure(config);

            _group = config.GetAsStringWithDefault("group", _group);
            _stream = config.GetAsStringWithDefault("stream", _stream);
        }

        /// <summary>
        /// Sets references to dependent components.
        /// </summary>
        /// <param name="references">references to locate the component dependencies.</param>
        public override void SetReferences(IReferences references)
        {
            base.SetReferences(references);
            _connectionResolver.SetReferences(references);
        }

        /// <summary>
        /// Writes a log message to the logger destination.
        /// </summary>
        /// <param name="level">a log level.</param>
        /// <param name="correlationId">(optional) transaction id to trace execution through call chain.</param>
        /// <param name="error">an error object associated with this message.</param>
        /// <param name="message">a human-readable message to log.</param>
        protected override void Write(LogLevel level, string correlationId, Exception error, string message)
        {
            if (Level < level)
            {
                return;
            }

            base.Write(level, correlationId, error, message);
        }

        /// <summary>
        /// Checks if the component is opened.
        /// </summary>
        /// <returns>true if the component has been opened and false otherwise.</returns>
        public bool IsOpen()
        {
            return _timer != null;
        }

        /// <summary>
        /// Opens the component.
        /// </summary>
        /// <param name="correlationId">(optional) transaction id to trace execution through call chain.</param>
        public async Task OpenAsync(string correlationId)
        {
            if (IsOpen()) return;

            var awsConnection = await _connectionResolver.ResolveAsync(correlationId);

            // Assign service name
            awsConnection.Service = "logs";
            awsConnection.ResourceType = "log-group";

            if (!string.IsNullOrEmpty(awsConnection.Resource))
                _group = awsConnection.Resource;

            // Undefined stream creates a random stream on every connect
            if (string.IsNullOrEmpty(_stream))
                _stream = IdGenerator.NextLong();

            // Validate connection params
            var err = awsConnection.Validate(correlationId);
            if (err != null) throw err;

            // Create client
            var region = RegionEndpoint.GetBySystemName(awsConnection.Region);
            var config = new AmazonCloudWatchLogsConfig()
            {
                RegionEndpoint = region
            };
            _client = new AmazonCloudWatchLogsClient(awsConnection.AccessId, awsConnection.AccessKey, config);

            // Create a log group if needed
            try
            {
                await _client.CreateLogGroupAsync(
                    new CreateLogGroupRequest 
                    { 
                        LogGroupName = _group
                    }
                );
            }
            catch (ResourceAlreadyExistsException)
            {
                // Ignore. Everything is ok
            }

            // Create or read log stream
            try
            {
                await _client.CreateLogStreamAsync(
                    new CreateLogStreamRequest
                    {
                        LogGroupName = _group,
                        LogStreamName = _stream
                    }
                );

                _lastToken = null;
            }
            catch (ResourceAlreadyExistsException)
            {
                var response = await _client.DescribeLogStreamsAsync(
                    new DescribeLogStreamsRequest
                    {
                        LogGroupName = _group,
                        LogStreamNamePrefix = _stream
                    }
                );

                if (response.LogStreams.Count > 0)
                {
                    _lastToken = response.LogStreams[0].UploadSequenceToken;
                }
            }

            if (_timer == null)
            {
                _timer = new FixedRateTimer(OnTimer, _interval, _interval);
                _timer.Start();
            }
        }

        /// <summary>
        /// Closes component and frees used resources.
        /// </summary>
        /// <param name="correlationId">(optional) transaction id to trace execution through call chain.</param>
        public async Task CloseAsync(string correlationId)
        {
            // Log all remaining messages before closing
            Dump();

            if (_timer != null)
            {
                _timer.Stop();
                _timer = null;
            }

            _client = null;

            await Task.Delay(0);
        }

        private void OnTimer()
        {
            Dump();
        }

        private string FormatMessageText(LogMessage message)
        {
            var build = new StringBuilder();
            build.Append('[');
            build.Append(message.Source ?? "---");
            build.Append(':');
            build.Append(message.CorrelationId ?? "---");
            build.Append(':');
            build.Append(message.Level.ToString());
            build.Append("] ");

            build.Append(message.Message);

            if (message.Error != null)
            {
                if (string.IsNullOrEmpty(message.Message))
                    build.Append("Error: ");
                else
                    build.Append(": ");

                build.Append(message.Error.Message);

                if (!string.IsNullOrEmpty(message.Error.StackTrace))
                {
                    build.Append(" StackTrace: ");
                    build.Append(message.Error.StackTrace);
                }
            }

            return build.ToString();
        }

        /// <summary>
        /// Saves log messages from the cache.
        /// </summary>
        /// <param name="messages">a list with log messages</param>
        protected override void Save(List<LogMessage> messages)
        {
            if (messages == null || messages.Count == 0) return;

            if (_client == null)
            {
                throw new InvalidStateException(
                    "cloudwatch_logger", "NOT_OPENED", "CloudWatchLogger is not opened"
                );
            }

            lock (_lock)
            {
                var events = new List<InputLogEvent>();
                foreach (var message in messages)
                {
                    events.Add(new InputLogEvent
                    {
                        Timestamp = message.Time,
                        Message = FormatMessageText(message)
                    });
                }

                try
                {
                    var result = _client.PutLogEventsAsync(new PutLogEventsRequest
                        {
                            LogGroupName = _group,
                            LogStreamName = _stream,
                            SequenceToken = _lastToken,
                            LogEvents = events
                        }
                    ).Result;

                    _lastToken = result.NextSequenceToken;
                }
                catch
                {
                    // Do nothing if elastic search client was not enable to process bulk of messages
                }
            }
        }
    }}
