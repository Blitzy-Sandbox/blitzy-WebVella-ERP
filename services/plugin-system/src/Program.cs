using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;

/// <summary>
/// Lambda bootstrap entry point for self-contained deployment on provided.al2023 runtime.
/// Reads _HANDLER env var (format: Assembly::Namespace.Type::Method), resolves
/// the handler via reflection, and dispatches invocations through the Lambda Runtime API.
/// </summary>
public static class LambdaEntryPoint
{
    private static readonly DefaultLambdaJsonSerializer Serializer = new();

    public static async Task Main(string[] args)
    {
        var handlerString = Environment.GetEnvironmentVariable("_HANDLER") ?? "";
        var parts = handlerString.Split(new[] { "::" }, StringSplitOptions.None);
        if (parts.Length != 3)
            throw new InvalidOperationException(
                $"Invalid _HANDLER format: '{handlerString}'. Expected 'Assembly::Type::Method'");

        var typeName = parts[1];
        var methodName = parts[2];

        // Resolve handler type from the executing assembly
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var handlerType = assembly.GetType(typeName)
            ?? throw new TypeLoadException($"Handler type not found: {typeName}");

        // Instantiate handler (all our handlers have parameterless constructors)
        var handler = Activator.CreateInstance(handlerType)
            ?? throw new InvalidOperationException($"Failed to create handler instance: {typeName}");

        // Resolve method
        var method = handlerType.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
            ?? throw new MissingMethodException(typeName, methodName);

        var methodParams = method.GetParameters();

        // Build the invocation wrapper that bridges raw Stream ↔ typed handler methods
        using var wrapper = HandlerWrapper.GetHandlerWrapper(
            (Action<Stream, ILambdaContext, MemoryStream>)((inputStream, ctx, outputStream) =>
            {
                InvokeHandler(handler, method, methodParams, inputStream, ctx, outputStream);
            }));

        using var bootstrap = new LambdaBootstrap(wrapper);
        await bootstrap.RunAsync();
    }

    private static void InvokeHandler(
        object handler, MethodInfo method, ParameterInfo[] methodParams,
        Stream inputStream, ILambdaContext ctx, MemoryStream outputStream)
    {
        // Build argument array matching the handler method's parameter list
        var invokeArgs = new object?[methodParams.Length];
        for (int i = 0; i < methodParams.Length; i++)
        {
            var pType = methodParams[i].ParameterType;
            if (pType == typeof(ILambdaContext) || pType.IsAssignableFrom(typeof(ILambdaContext)))
            {
                invokeArgs[i] = ctx;
            }
            else if (pType == typeof(Stream))
            {
                invokeArgs[i] = inputStream;
            }
            else
            {
                // Deserialize typed input (APIGatewayHttpApiV2ProxyRequest, SQSEvent, etc.)
                invokeArgs[i] = DeserializeInput(inputStream, pType);
            }
        }

        // Invoke the handler method
        var result = method.Invoke(handler, invokeArgs);

        // Process the result — handle async (Task<T>) and sync returns
        if (result is Task task)
        {
            task.GetAwaiter().GetResult();

            // Extract Task<T>.Result if the Task is generic
            var taskType = task.GetType();
            if (taskType.IsGenericType)
            {
                var resultValue = taskType.GetProperty("Result")?.GetValue(task);
                SerializeOutput(resultValue, outputStream);
            }
        }
        else
        {
            SerializeOutput(result, outputStream);
        }
    }

    private static object? DeserializeInput(Stream inputStream, Type targetType)
    {
        // Use reflection to call Serializer.Deserialize<T>(Stream) with runtime type
        var deserializeMethod = typeof(DefaultLambdaJsonSerializer)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == "Deserialize" && m.IsGenericMethodDefinition
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType == typeof(Stream))
            .MakeGenericMethod(targetType);

        return deserializeMethod.Invoke(Serializer, new object[] { inputStream });
    }

    private static void SerializeOutput(object? value, Stream outputStream)
    {
        if (value == null) return;

        if (value is Stream stream)
        {
            stream.CopyTo(outputStream);
            return;
        }

        // Use reflection to call Serializer.Serialize<T>(T, Stream) with runtime type
        var serializeMethod = typeof(DefaultLambdaJsonSerializer)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == "Serialize" && m.IsGenericMethodDefinition
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[1].ParameterType == typeof(Stream))
            .MakeGenericMethod(value.GetType());

        serializeMethod.Invoke(Serializer, new object[] { value, outputStream });
    }
}
