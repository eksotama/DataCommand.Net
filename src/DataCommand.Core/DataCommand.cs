﻿using Microsoft.Extensions.Logging;
using Polly;
using System;
using System.Data;
using System.Diagnostics;

namespace DataCommand.Core
{
    /// <summary>
    /// Represents a base class for data commands to be executed against the database.
    /// </summary>
    /// <example>
    ///     <code>
    ///         public class MySpecificCommand : DataCommand&lt;Entity&gt;
    ///         {
    ///             private int _entityId;        
    /// 
    ///             public MySpecificCommand(int entityId, DataCommandOptions options, ILoggerFactory factory)
    ///                 : base("MySpecificCommand", options, factory)
    ///             {
    ///                 _entityId = entityId;
    ///             }
    ///             
    ///             protected override Entity Execute(IDbConnection connection, DataCommandOptions options)
    ///             {
    ///                 Entity entity = null;
    ///                 
    ///                 // Do some stuff to retrieve the entity by id (_entityId)
    ///                 
    ///                 return entity;
    ///             }
    ///         }
    ///     </code>
    /// </example>
    /// <typeparam name="T">The type of the command's return value.</typeparam>
    public abstract class DataCommand<T>
    {
        #region Private Fields

        private DataCommandOptions _options;

        #endregion

        /// <summary>
        /// Gets the name for this data command.
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// Gets this command's execution statistics.
        /// </summary>
        public CommandStatistics Statistics { get; private set; }

        /// <summary>
        /// Gets the default logger for this command.
        /// </summary>
        protected ILogger Logger { get; private set; }

        #region Constructors

        /// <summary>
        ///     <para>
        ///         Initializes a new instance of the <see cref="DataCommand{T}"/> class using the specified name and options.
        ///     </para>
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Usually, the data command's name is passed by child classes to identify themselves.
        ///         It is an ID for the command, that will be found on logs and statistics. 
        ///     </para>
        /// </remarks>
        /// <param name="name">The name of this data command.</param>
        /// <param name="options">The options to use.</param>
        /// <param name="loggerFactory">The Factory Service to be used when creating loggers for this command.</param>
        protected DataCommand(string name, DataCommandOptions options, ILoggerFactory loggerFactory)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentNullException("name");
            if (null == options) throw new ArgumentNullException("options");
            if (null == loggerFactory) throw new ArgumentNullException("loggerFactory");

            // Test if a connection string was provided
            if (string.IsNullOrWhiteSpace(options.ConnectionString)) throw new ArgumentException("A connection string must be supplied within options parameter.");

            //Setup command's name
            Name = name;

            //Setup internal command options
            _options = options;

            //Creates the default logger, that can be used by child classes
            Logger = loggerFactory.CreateLogger(GetType());

            //Creates the command statistics
            Statistics = new CommandStatistics() { Name = Name };
        }

        #endregion

        /// <summary>
        /// Runs this data command.
        /// </summary>
        /// <returns>A <see cref="T"/> instance as this operation's result.</returns>
        public T Run()
        {
            //Let's starti counting the total execution time
            Stopwatch totalWatch = Stopwatch.StartNew();

            //Setup the resulting object with its default value
            T resultObject = default(T);

            //First step, let's handle the connection.
            IDbConnection connection = CreateConnection();


            // Define the open connection and execution policies
            var openConnectionPolicy =
                Policy
                    .Handle<Exception>((ex) => HandleOpenConnectionException(ex))
                    .Retry(_options.MaxRetries, (ex, retryCount, context) =>
                        {
                            Logger.LogWarning(
                                CommandEventId.ConnectionError, ex, $"Error while trying to open the connection. Retry count is {retryCount}.");
                        });

            var executeConnectionPolicy =
                Policy
                    .Handle<Exception>((ex) => HandleExecutionException(ex))
                    .Retry(_options.MaxRetries, (ex, retryCount, context) =>
                    {
                        Logger.LogWarning(
                            CommandEventId.ConnectionError, ex, $"Error while trying to execute this command. Retry count is {retryCount}.");
                    });

            //Let's use the connection, so it can be disposed later
            using (connection)
            {
                var result = openConnectionPolicy.ExecuteAndCapture(() =>
                {
                    //Let's try open a connection to database.
                    connection.Open();
                });

                if(result.FinalException != null)
                {
                    totalWatch.Stop();

                    //Update statistics
                    Statistics.LastElapsedTime = totalWatch.Elapsed;

                    throw result.FinalException;
                }

                Stopwatch executionWatch = Stopwatch.StartNew();

                //Now, let's try to execute the command
                var execResult = executeConnectionPolicy.ExecuteAndCapture<T>(() => Execute(connection, _options));

                executionWatch.Stop();
                totalWatch.Stop();

                Statistics.LastElapsedTime = totalWatch.Elapsed;
                Statistics.LastExecElapsedTime = executionWatch.Elapsed;

                if (execResult.FinalException != null)
                {
                    throw execResult.FinalException;
                }
                resultObject = execResult.Result;
            }

            return resultObject;
        }

        /// <summary>
        /// Tries to handle exceptions during the command's execution.
        /// </summary>
        /// <param name="exception">The exception to handle</param>
        /// <returns><c>true</c>, if the exception could be handled and the command should proceed with normal execution. <c>false</c>, otherwise.</returns>
        protected bool HandleExecutionException(Exception exception)
        {
            return _options.ShouldRetryOn(exception);
        }

        /// <summary>
        /// Tries to handle exceptions while opening connections to the database.
        /// </summary>
        /// <param name="ex">The exception to handle</param>
        /// <returns><c>true</c>, if the exception could be handled and the command should proceed with normal execution. <c>false</c>, otherwise.</returns>
        protected bool HandleOpenConnectionException(Exception ex)
        {
            return _options.ShouldRetryOn(ex);
        }

        /// <summary>
        /// Creates a new <see cref="IDbConnection"/> database connection, without opening it.
        /// </summary>
        /// <returns></returns>
        protected virtual IDbConnection CreateConnection()
        {
            var connection = _options.CreateConnection();

            // Check if it is a valid connection
            if (connection == null)
                throw new InvalidOperationException("An invalid connection (null) was supplied by the received options.");

            return connection;
        }

        /// <summary>
        /// Executes the data command inside the provided connection.
        /// </summary>
        /// <param name="connection">The connection to the database to be used.</param>
        /// <param name="options">The options for this command execution.</param>
        /// <returns>A <see cref="T"/> instance, result from the execution within this connection.</returns>
        protected abstract T Execute(IDbConnection connection, DataCommandOptions options);

    }
}
