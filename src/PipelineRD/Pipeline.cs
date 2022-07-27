﻿using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

using Polly;

using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using PipelineRD.Cache;

namespace PipelineRD;

public class Pipeline<TContext, TRequest> : IPipeline<TContext, TRequest> where TContext : BaseContext
{
    public Queue<Handler<TContext, TRequest>> Handlers { get; private set; }
    public TContext Context { get; private set; }
    private string Identifier => $"Pipeline<{typeof(TContext).Name}, {typeof(TRequest).Name}>";

    private readonly IServiceProvider _serviceProvider;
    private ICacheProvider _cacheProvider;
    private bool _useCache;
    private string _currentHandlerIdentifier;


    public Pipeline(IServiceProvider serviceProvider, TContext context = null) : this()
    {
        _serviceProvider = serviceProvider;
        Context = context ?? serviceProvider.GetService<TContext>() ?? throw new PipelineException($"{typeof(TContext).Name} not found in the dependency container.");
    }

    protected Pipeline()
    {
        Handlers = new Queue<Handler<TContext, TRequest>>();
    }

    public IPipeline<TContext, TRequest> EnableCache(ICacheProvider cacheProvider = null)
    {
        if(_serviceProvider.GetService<IDistributedCache>() == null)
        {
            throw new PipelineException("IDistributedCache interface is not injected.");
        }

        _cacheProvider = (cacheProvider ?? _serviceProvider.GetService<ICacheProvider>()) ?? throw new PipelineException($"CacheProvider not found in the dependency container.");
        _useCache = true;
        return this;
    }

    public IPipeline<TContext, TRequest> DisableCache()
    {
        _useCache = false;
        return this;
    }

    public HandlerResult Execute(TRequest request)
        => Execute(request, string.Empty, string.Empty);

    public HandlerResult Execute(TRequest request, string idempotencyKey)
        => Execute(request, idempotencyKey, string.Empty);

    public HandlerResult Execute(TRequest request, string idempotencyKey, string initialHandlerIdentifier)
    {
        var hash = GetRequestHash(request, idempotencyKey);

        initialHandlerIdentifier ??= string.Empty;

        if (_useCache)
        {
            var snapshot = _cacheProvider.Get<PipelineSnapshot<TContext>>(hash);
            if (snapshot != null)
            {
                if (snapshot.Success)
                {
                    return snapshot.Context.Result;
                }
                else
                {
                    Context = snapshot.Context;
                    initialHandlerIdentifier = snapshot.HandlerIdentifier;
                }
            }
        }

        var result = ExecutePipeline(request, initialHandlerIdentifier);

        if (_useCache)
        {
            PipelineSnapshot<TContext> snapshot = new(
                result.IsSuccess,
                _currentHandlerIdentifier,
                Context
            );

            _cacheProvider.Add(snapshot, hash);
        }

        return result;
    }

    private HandlerResult ExecutePipeline(TRequest request, string initialHandlerIdentifier)
    {
        var handler = DequeueHandler();

        // Return default result when pipeline does not have a handler
        if (handler == null)
        {
            return GetResult();
        }

        // Ensure that the Context is the same to all executed steps
        handler.DefineContext(Context);

        // If startHandlerIdentifier is empty, it means that did not use or find any snapshot cache
        // The second condition means that will ignore the recursive right execution order until the current handler
        // in execution is equal to the one that we got from the snapshot cache, and it will start from that
        if (ExecuteInOrder() || ExecuteFromHandler())
        {
            ExecuteHandler();
        }
        // If it is not an ordered execution, it will check if the handler has a recovery
        else if(!ExecuteInOrder() && HandlerHasRecovery())
        {
            ExecuteRecoveryHandler();
        }

        return IsFinished() switch
        {
            true => GetResult(),
            false => ExecutePipeline(request, initialHandlerIdentifier)
        };

        bool ExecuteInOrder()
            => initialHandlerIdentifier == string.Empty;

        bool ExecuteFromHandler()
        {
            var result = initialHandlerIdentifier == handler.Identifier;
            // Reset the initial handler identifier to execute the next steps
            // in order if not empty
            if (result)
                initialHandlerIdentifier = string.Empty;
            return result;
        }

        void ExecuteHandler()
        {
            // Execute step based on condition if defined
            if (handler.Condition is null || handler.Condition.Compile().Invoke(handler.Context))
            {
                if(handler.Policy != null)
                {
                    handler.Policy.Execute(() =>
                    {
                        handler.Handle(request);
                        return handler.Result ?? new();
                    });
                }
                else
                {
                    handler.Handle(request);
                }
            }
        }

        bool HandlerHasRecovery()
            => handler.RecoveryHandler != null;

        void ExecuteRecoveryHandler()
        {
            handler.RecoveryHandler.Handle(request);
        }
    }

    public string GetRequestHash(TRequest request, string idempotencyKey)
    {
        return string.IsNullOrEmpty(idempotencyKey) ?
            GenerateRequestHash(request) :
            idempotencyKey;

        string GenerateRequestHash(TRequest request)
        {
            var requestString = $"{Identifier}: {RequestToString(request)}";
            var encoding = new ASCIIEncoding();
            var key = encoding.GetBytes("072e77e426f92738a72fe23c4d1953b4");
            var hmac = new HMACSHA1(key);
            var bytes = hmac.ComputeHash(encoding.GetBytes(requestString));
            return Convert.ToBase64String(bytes);
        }

        static string RequestToString(TRequest request)
            => JsonSerializer.Serialize(request);
    }

    public IPipeline<TContext, TRequest> When(Expression<Func<TContext, bool>> condition)
    {
        var step = Handlers.LastOrDefault();
        if (step != null)
        {
            step.DefineConditionToExecution(condition);
        }
        return this;
    }

    public IPipeline<TContext, TRequest> WithHandler<THandler>() where THandler : Handler<TContext, TRequest>
    {
        var handler = _serviceProvider.GetService<THandler>() ?? throw new PipelineException($"{typeof(THandler).Name} not found in the dependency container.");
        return WithHandler(handler);
    }

    public IPipeline<TContext, TRequest> WithHandler(Handler<TContext, TRequest> handler)
    {
        handler.DefineContext(Context);
        Handlers.Enqueue(handler);
        return this;
    }

    public IPipeline<TContext, TRequest> WithPolicy(Policy<HandlerResult> policy)
    {
        var step = Handlers.LastOrDefault();
        if (policy != null && step != null)
        {
            step.DefinePolicy(policy);
        }

        return this;
    }

    public IPipeline<TContext, TRequest> WithRecovery<TRecoveryHandler>() where TRecoveryHandler : RecoveryHandler<TContext, TRequest>
    {
        var handler = _serviceProvider.GetService<TRecoveryHandler>() ?? throw new PipelineException($"Recovery {typeof(TRecoveryHandler).Name} not found in the dependency container.");
        return WithRecovery(handler);
    }

    public IPipeline<TContext, TRequest> WithRecovery(RecoveryHandler<TContext, TRequest> recoveryHandler)
    {
        var handler = Handlers.LastOrDefault();
        if (recoveryHandler != null && handler != null)
        {
            recoveryHandler.DefineContext(Context);
            handler.DefineRecovery(recoveryHandler);
        }

        return this;
    }

    private HandlerResult GetResult()
     => Context.Result ?? HandlerResult.NoResult();

    private bool IsFinished()
        => !Handlers.Any() || Context.Result != null;

    private Handler<TContext, TRequest> DequeueHandler()
    {
        var handler = Handlers.Dequeue();
        _currentHandlerIdentifier = handler.Identifier;
        return handler;
    }
}
