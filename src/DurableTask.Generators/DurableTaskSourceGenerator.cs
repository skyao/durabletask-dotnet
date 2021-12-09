﻿//  ----------------------------------------------------------------------------------
//  Copyright Microsoft Corporation
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//  http://www.apache.org/licenses/LICENSE-2.0
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//  ----------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using DurableTask.Generators.AzureFunctions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace DurableTask.Generators
{
    [Generator]
    public class DurableTaskSourceGenerator : ISourceGenerator
    {
        /* Example input:
         * 
         * [DurableTask("MyActivity")]
         * class MyActivity : TaskActivityBase<CustomType, string>
         * {
         *     public Task<string> RunAsync(CustomType input)
         *     {
         *         string instanceId = this.Context.InstanceId;
         *         // ...
         *         return input.ToString();
         *     }
         * }
         * 
         * Example output:
         * 
         * public static Task<string> CallMyActivityAsync(this TaskOrchestrationContext ctx, int input, TaskOptions? options = null)
         * {
         *     return ctx.CallActivityAsync("MyActivity", input, options);
         * }
         */

        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new DurableTaskSyntaxReceiver());
        }

        public void Execute(GeneratorExecutionContext context)
        {
            // This generator also supports Durable Functions for .NET isolated, but we only generate Functions-specific
            // code if we find the Durable Functions extention listed in the set of referenced assembly names.
            bool isDurableFunctions = context.Compilation.ReferencedAssemblyNames.Any(
                assembly => assembly.Name.Equals("Microsoft.Azure.Functions.Worker.Extensions.DurableTask", StringComparison.OrdinalIgnoreCase));

            // Enumerate all the activities in the project
            // the generator infrastructure will create a receiver and populate it
            // we can retrieve the populated instance via the context
            if (context.SyntaxContextReceiver is not DurableTaskSyntaxReceiver receiver)
            {
                // Unexpected receiver came back?
                return;
            }

            int found = receiver.Activities.Count + receiver.Orchestrators.Count + receiver.DurableFunctions.Count;
            if (found == 0)
            {
                // Didn't find anything
                return;
            }

            StringBuilder sourceBuilder = new(capacity: found * 1024);
            sourceBuilder.Append(@"// <auto-generated/>
#nullable enable

using System;
using System.Threading.Tasks;");

            if (isDurableFunctions)
            {
                sourceBuilder.Append(@"
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;");
            }

            sourceBuilder.Append(@"

namespace DurableTask
{
    public static class GeneratedDurableTaskExtensions
    {");
            if (isDurableFunctions)
            {
                // Generate a singleton orchestrator object instance that can be reused for all invocations.
                foreach (DurableTaskTypeInfo orchestrator in receiver.Orchestrators)
                {
                    sourceBuilder.AppendLine($@"
        static readonly {orchestrator.TypeName} singleton{orchestrator.TaskName} = new {orchestrator.TypeName}();");
                }
            }

            foreach (DurableTaskTypeInfo orchestrator in receiver.Orchestrators)
            {
                if (isDurableFunctions)
                {
                    // Generate the function definition required to trigger orchestrators in Azure Functions
                    AddOrchestratorFunctionDeclaration(sourceBuilder, orchestrator);
                }

                AddOrchestratorCallMethod(sourceBuilder, orchestrator);
                AddSubOrchestratorCallMethod(sourceBuilder, orchestrator);
            }

            foreach (DurableTaskTypeInfo activity in receiver.Activities)
            {
                AddActivityCallMethod(sourceBuilder, activity);

                if (isDurableFunctions)
                {
                    // Generate the function definition required to trigger activities in Azure Functions
                    AddActivityFunctionDeclaration(sourceBuilder, activity);
                }
            }

            // Activity function triggers are supported for code-gen (but not orchestration triggers)
            IEnumerable<DurableFunction> activityTriggers = receiver.DurableFunctions.Where(
                df => df.Kind == DurableFunctionKind.Activity);
            foreach (DurableFunction function in activityTriggers)
            {
                AddActivityCallMethod(sourceBuilder, function);
            }

            if (isDurableFunctions)
            {
                if (receiver.Activities.Count > 0)
                {
                    // Functions-specific helper class, which is only needed when
                    // using the class-based syntax.
                    AddGeneratedActivityContextClass(sourceBuilder);
                }
            }
            else
            {
                // ASP.NET Core-specific service registration methods
                AddRegistrationMethodForAllTasks(
                    sourceBuilder,
                    receiver.Orchestrators,
                    receiver.Activities);
            }

            sourceBuilder.AppendLine("    }").AppendLine("}");

            context.AddSource("GeneratedDurableTaskExtensions.cs", SourceText.From(sourceBuilder.ToString(), Encoding.UTF8, SourceHashAlgorithm.Sha256));
        }

        static void AddOrchestratorFunctionDeclaration(StringBuilder sourceBuilder, DurableTaskTypeInfo orchestrator)
        {
            sourceBuilder.AppendLine($@"
        [Function(nameof({orchestrator.TaskName}))]
        public static async Task<string> {orchestrator.TaskName}([OrchestrationTrigger] string orchestratorState)
        {{
            return await DurableOrchestrator.LoadAndRunAsync<{orchestrator.InputType}, {orchestrator.OutputType}>(orchestratorState, singleton{orchestrator.TaskName});
        }}");
        }

        static void AddOrchestratorCallMethod(StringBuilder sourceBuilder, DurableTaskTypeInfo orchestrator)
        {
            sourceBuilder.AppendLine($@"
        /// <inheritdoc cref=""DurableTaskClient.ScheduleNewOrchestrationInstanceAsync""/>
        public static Task<string> ScheduleNew{orchestrator.TaskName}InstanceAsync(
            this DurableTaskClient client,
            string? instanceId = null,
            {orchestrator.InputDefaultType} input = default,
            DateTimeOffset? startTime = null)
        {{
            return client.ScheduleNewOrchestrationInstanceAsync(
                ""{orchestrator.TaskName}"",
                instanceId,
                input,
                startTime);
        }}");
        }

        static void AddSubOrchestratorCallMethod(StringBuilder sourceBuilder, DurableTaskTypeInfo orchestrator)
        {
            sourceBuilder.AppendLine($@"
        /// <inheritdoc cref=""TaskOrchestrationContext.CallSubOrchestratorAsync""/>
        public static Task<{orchestrator.OutputType}> Call{orchestrator.TaskName}Async(
            this TaskOrchestrationContext context,
            string? instanceId = null,
            {orchestrator.InputDefaultType} input = default,
            TaskOptions? options = null)
        {{
            return context.CallSubOrchestratorAsync<{orchestrator.OutputType}>(
                ""{orchestrator.TaskName}"",
                instanceId,
                input,
                options);
        }}");
        }

        static void AddActivityCallMethod(StringBuilder sourceBuilder, DurableTaskTypeInfo activity)
        {
            sourceBuilder.AppendLine($@"
        public static Task<{activity.OutputType}> Call{activity.TaskName}Async(this TaskOrchestrationContext ctx, {activity.InputType} input, TaskOptions? options = null)
        {{
            return ctx.CallActivityAsync<{activity.OutputType}>(""{activity.TaskName}"", input, options);
        }}");
        }

        static void AddActivityCallMethod(StringBuilder sourceBuilder, DurableFunction activity)
        {
            sourceBuilder.AppendLine($@"
        public static Task<{activity.ReturnType}> Call{activity.Name}Async(this TaskOrchestrationContext ctx, {activity.Parameter}, TaskOptions? options = null)
        {{
            return ctx.CallActivityAsync<{activity.ReturnType}>(""{activity.Name}"", {activity.Parameter.Name}, options);
        }}");
        }

        static void AddActivityFunctionDeclaration(StringBuilder sourceBuilder, DurableTaskTypeInfo activity)
        {
            // GeneratedActivityContext is a generated class that we use for each generated activity trigger definition.
            // Note that the second "instanceId" parameter is populated via the Azure Functions binding context.
            sourceBuilder.AppendLine($@"
        [Function(nameof({activity.TaskName}))]
        public static async Task<{activity.OutputType}> {activity.TaskName}([ActivityTrigger] {activity.InputDefaultType} input, string instanceId, FunctionContext executionContext)
        {{
            ITaskActivity activity = ActivatorUtilities.CreateInstance<{activity.TypeName}>(executionContext.InstanceServices);
            ITaskActivityContext context = new GeneratedActivityContext(""{activity.TaskName}"", instanceId, input);
            object? result = await activity.RunAsync(context);
            return ({activity.OutputType})result!;
        }}");
        }

        /// <summary>
        /// Adds a custom ITaskActivityContext implementation used by code generated from <see cref="AddActivityFunctionDeclaration"/>.
        /// </summary>
        static void AddGeneratedActivityContextClass(StringBuilder sourceBuilder)
        {
            // NOTE: Any breaking changes to ITaskActivityContext need to be reflected here as well.
            sourceBuilder.AppendLine(GetGeneratedActivityContextCode());
        }

        // This is public so that it can be called by unit test code.
        public static string GetGeneratedActivityContextCode() => $@"
        sealed class GeneratedActivityContext : ITaskActivityContext
        {{
            readonly object? input;

            public GeneratedActivityContext(TaskName name, string instanceId, object? input)
            {{
                this.Name = name;
                this.InstanceId = instanceId;
                this.input = input;
            }}

            public TaskName Name {{ get; }}

            public string InstanceId {{ get; }}

            public T? GetInput<T>() => (T?)(this.input ?? default);
        }}";

        static void AddRegistrationMethodForAllTasks(
            StringBuilder sourceBuilder,
            IEnumerable<DurableTaskTypeInfo> orchestrators,
            IEnumerable<DurableTaskTypeInfo> activities)
        {
            sourceBuilder.Append($@"
        public static ITaskBuilder AddAllGeneratedTasks(this ITaskBuilder builder)
        {{");

            foreach (DurableTaskTypeInfo taskInfo in orchestrators)
            {
                sourceBuilder.Append($@"
            builder.AddOrchestrator<{taskInfo.TypeName}>();");
            }

            foreach (DurableTaskTypeInfo taskInfo in activities)
            {
                sourceBuilder.Append($@"
            builder.AddActivity<{taskInfo.TypeName}>();");
            }

            sourceBuilder.AppendLine($@"
            return builder;
        }}");
        }

        class DurableTaskSyntaxReceiver : ISyntaxContextReceiver
        {
            readonly List<DurableTaskTypeInfo> orchestrators = new();
            readonly List<DurableTaskTypeInfo> activities = new();
            readonly List<DurableFunction> durableFunctions = new();

            public IReadOnlyList<DurableTaskTypeInfo> Orchestrators => this.orchestrators;
            public IReadOnlyList<DurableTaskTypeInfo> Activities => this.activities;
            public IReadOnlyList<DurableFunction> DurableFunctions => this.durableFunctions;

            public void OnVisitSyntaxNode(GeneratorSyntaxContext context)
            {
                if (context.Node is MethodDeclarationSyntax method &&
                    DurableFunction.TryParse(context.SemanticModel, method, out DurableFunction? function) &&
                    function != null)
                {
                    Debug.WriteLine($"Adding {function.Kind} function '{function.Name}'");
                    this.durableFunctions.Add(function);
                    return;
                }

                if (context.Node is BaseListSyntax baseList && baseList.Parent is TypeDeclarationSyntax typeSyntax)
                {
                    // TODO: Validate that the type is not an abstract class

                    foreach (BaseTypeSyntax baseType in baseList.Types)
                    {
                        if (baseType.Type is not GenericNameSyntax genericName)
                        {
                            // This is not the type we're looking for
                            continue;
                        }

                        // TODO: Find a way to use the semantic model to do this so that we can support
                        //       custom base types that derive from our special base types.
                        List<DurableTaskTypeInfo> taskList;
                        if (genericName.Identifier.ValueText == "TaskActivityBase")
                        {
                            taskList = this.activities;
                        }
                        else if (genericName.Identifier.ValueText == "TaskOrchestratorBase")
                        {
                            taskList = this.orchestrators;
                        }
                        else
                        {
                            // This is not the type we're looking for
                            continue;
                        }

                        string typeName;
                        if (context.SemanticModel.GetDeclaredSymbol(typeSyntax) is not ISymbol typeSymbol)
                        {
                            // Invalid type declaration?
                            continue;
                        };

                        typeName = typeSymbol.ToDisplayString();

                        ITypeSymbol? inputType = null;
                        ITypeSymbol? outputType = null;
                        if (genericName.TypeArgumentList.Arguments.Count > 0)
                        {
                            inputType = context.SemanticModel.GetTypeInfo(genericName.TypeArgumentList.Arguments[0]).Type;
                        }

                        if (genericName.TypeArgumentList.Arguments.Count > 1)
                        {
                            outputType = context.SemanticModel.GetTypeInfo(genericName.TypeArgumentList.Arguments[1]).Type;
                        }

                        // By default, the task name is the class name.
                        string taskName = typeSyntax.Identifier.ValueText; // TODO: What if the class has generic type parameters?;

                        // If a [DurableTask(name)] attribute is present, use that as the activity name.
                        foreach (AttributeSyntax attribute in typeSyntax.AttributeLists.SelectMany(list => list.Attributes))
                        {
                            ITypeSymbol? attributeType = context.SemanticModel.GetTypeInfo(attribute.Name).Type;
                            if (attributeType?.ToString() == "DurableTask.DurableTaskAttribute" &&
                                attribute.ArgumentList?.Arguments.Count > 0)
                            {
                                ExpressionSyntax expression = attribute.ArgumentList.Arguments[0].Expression;
                                taskName = context.SemanticModel.GetConstantValue(expression).ToString();
                                break;
                            }
                        }

                        taskList.Add(new DurableTaskTypeInfo(
                            typeName,
                            taskName,
                            inputType,
                            outputType));

                        break;
                    }
                }
            }
        }

        class DurableTaskTypeInfo
        {
            public DurableTaskTypeInfo(
                string taskType,
                string taskName,
                ITypeSymbol? inputType,
                ITypeSymbol? outputType)
            {
                this.TypeName = taskType;
                this.TaskName = taskName;
                this.InputType = GetRenderedTypeExpression(inputType, supportsNullable: false);
                this.InputDefaultType = GetRenderedTypeExpression(inputType, supportsNullable: true);
                this.OutputType = GetRenderedTypeExpression(outputType, supportsNullable: false);
            }

            public string TypeName { get; }
            public string TaskName { get; }
            public string InputType { get; }
            public string InputDefaultType { get; }
            public string OutputType { get; }

            static string GetRenderedTypeExpression(ITypeSymbol? symbol, bool supportsNullable)
            {
                if (symbol == null)
                {
                    return supportsNullable ? "object?" : "object";
                }

                if (supportsNullable && symbol.IsReferenceType && symbol.NullableAnnotation != NullableAnnotation.Annotated)
                {
                    symbol = symbol.WithNullableAnnotation(NullableAnnotation.Annotated);
                }

                string expression = symbol.ToString();
                if (expression.StartsWith("System.") && symbol.ContainingNamespace.Name == "System")
                {
                    expression = expression.Substring("System.".Length);
                }

                return expression;
            }
        }
    }
}