﻿using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace BehaviorGenerator;

[Generator]
public class BehaviorGenerator : IIncrementalGenerator
{
    /// <summary>
    /// Behavior partial类上下文
    /// </summary>
    /// <param name="FileName"></param>
    /// <param name="DelegateContext"></param>
    private record struct BehaviorFileContext(string NamespaceName, string FileName,
        ImmutableArray<EventHandlerContext> DelegateContext);

    /// <summary>
    /// event handler上下文
    /// </summary>
    /// <param name="EventHandlerName">EventHandler的名称</param>
    /// <param name="SignalName">信号的名称</param>
    /// <param name="ParamNames">参数名称列表</param>
    private record struct EventHandlerContext(string EventHandlerName, string SignalName,
        ImmutableArray<string> ParamNames);

    /// <summary>
    /// 文件名 -> BehaviorFileContext
    /// </summary>
    private readonly Dictionary<string, BehaviorFileContext> _behaviorFileContexts = new();

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
#if DEBUG
        if (!Debugger.IsAttached)
        {
            Debugger.Launch();
            SpinWait.SpinUntil(() => Debugger.IsAttached, 5000);
        }
#endif

        var valuesProvider = context.SyntaxProvider.CreateSyntaxProvider(SyntacticPredicate, SemanticTransform);

        context.RegisterSourceOutput(valuesProvider, Execute);
    }

    private void Execute(SourceProductionContext sourceProductionContext,
        BehaviorFileContext behaviorFileContext)
    {
        var signalMethod = behaviorFileContext.FileName.Equals("Behavior.cs")
            ? $"{GenerateConnectSignalMethod()}\n\n{GenerateDisconnectSignalMethod()}"
            : "";
        var signalField = behaviorFileContext.FileName.Equals("Behavior.cs") ? $"{GenerateStaticField()}\n" : "";

        var getEventHandlerMethod = new StringBuilder(1024);
        foreach (var eventHandlerContext in behaviorFileContext.DelegateContext)
        {
            var paramJoin = string.Join(", ", eventHandlerContext.ParamNames);
            var paramSeparator = eventHandlerContext.ParamNames.IsEmpty ? "" : ", ";
            var signalName = eventHandlerContext.SignalName;
            var eventHandlerName = eventHandlerContext.EventHandlerName;
            var eventHandler = $@"    private {eventHandlerName} Get{eventHandlerName}()
    {{
        return ({paramJoin}) => OnSignal(SignalName.{signalName}{paramSeparator}{paramJoin});
    }}

";
            getEventHandlerMethod.Append(eventHandler);
        }

        var source = $@"// <auto-generated>

namespace {behaviorFileContext.NamespaceName};

public partial class Behavior
{{
{signalField}
{getEventHandlerMethod}
{signalMethod}
}}
";

        sourceProductionContext.AddSource($"{behaviorFileContext.FileName.Replace("cs", "g.cs")}", source);
    }

    private bool SyntacticPredicate(SyntaxNode n, CancellationToken cancellationToken)
    {
        if (n is not ClassDeclarationSyntax classDeclarationSyntax)
        {
            return false;
        }

        if (!classDeclarationSyntax.Identifier.Text.Equals("Behavior"))
        {
            return false;
        }

        return true;
    }

    private BehaviorFileContext SemanticTransform(GeneratorSyntaxContext context,
        CancellationToken cancellationToken)
    {
        Debug.Assert(context.Node is ClassDeclarationSyntax);

        var semanticModel = context.SemanticModel;
        //获取当前正在访问的Node
        var classSyntax = Unsafe.As<ClassDeclarationSyntax>(context.Node);
        var fileName = Path.GetFileName(classSyntax.SyntaxTree.FilePath);
        var namespaceName = GetNamespace(classSyntax);

        //找到所有的delegate定义
        var delegateSyntaxS = classSyntax.DescendantNodes().OfType<DelegateDeclarationSyntax>();
        var eventHandlerContexts = delegateSyntaxS
            //转换成symbol
            .Select(delegateSyntax => semanticModel.GetDeclaredSymbol(delegateSyntax, cancellationToken))
            .Where(delegateSymbol => delegateSymbol != null)
            //至少有一个Attribute包含[Signal]
            .Where(delegateSymbol => delegateSymbol!.GetAttributes()
                .Any(attribute => attribute.AttributeClass?.Name == "SignalAttribute"))
            .Where(delegateSymbol => delegateSymbol!.Name.EndsWith("EventHandler"))
            .Select(eventHandlerSymbol => new EventHandlerContext(eventHandlerSymbol!.Name,
                eventHandlerSymbol.Name.Replace("EventHandler", ""),
                eventHandlerSymbol.DelegateInvokeMethod!.Parameters
                    .Select(param => param.Name)
                    .ToImmutableArray()))
            .ToImmutableArray();

        var behaviorFileContext = new BehaviorFileContext(namespaceName, fileName, eventHandlerContexts);
        _behaviorFileContexts[fileName] = behaviorFileContext;

        return behaviorFileContext;
    }

    private string GenerateConnectSignalMethod()
    {
        var customHandlerBuilder = new StringBuilder(1024);
        foreach (var behaviorFileContext in _behaviorFileContexts.Values)
        {
            foreach (var eventHandlerContext in behaviorFileContext.DelegateContext)
            {
                customHandlerBuilder.Append($@"        if (SignalName.{eventHandlerContext.SignalName}.Equals(signal))
        {{
            {eventHandlerContext.SignalName} += Get{eventHandlerContext.SignalName}EventHandler();
        }}

");
            }
        }

        //lang=c#
        var myString = @$"    private void ConnectSignal(Godot.StringName signal)
    {{
        if (Godot.GodotObject.SignalName.ScriptChanged.Equals(signal))
        {{
            ScriptChanged += () => OnSignal(Godot.GodotObject.SignalName.ScriptChanged);
        }}

        if (Godot.GodotObject.SignalName.PropertyListChanged.Equals(signal))
        {{
            PropertyListChanged += () => OnSignal(Godot.GodotObject.SignalName.PropertyListChanged);
        }}

        if (Godot.Node.SignalName.Ready.Equals(signal))
        {{
            Ready += () => OnSignal(Godot.Node.SignalName.Ready);
        }}

        if (Godot.Node.SignalName.Renamed.Equals(signal))
        {{
            Renamed += () => OnSignal(Godot.Node.SignalName.Renamed);
        }}

        if (Godot.Node.SignalName.TreeEntered.Equals(signal))
        {{
            TreeEntered += () => OnSignal(Godot.Node.SignalName.TreeEntered);
        }}

        if (Godot.Node.SignalName.TreeExiting.Equals(signal))
        {{
            TreeExiting += () => OnSignal(Godot.Node.SignalName.TreeExited);
        }}

        if (Godot.Node.SignalName.ChildEnteredTree.Equals(signal))
        {{
            ChildEnteredTree += node => OnSignal(Godot.Node.SignalName.ChildEnteredTree, node);
        }}

        if (Godot.Node.SignalName.ChildExitingTree.Equals(signal))
        {{
            ChildExitingTree += node => OnSignal(Godot.Node.SignalName.ChildExitingTree, node);
        }}
        
{customHandlerBuilder}
    }}";
        return myString;
    }

    private string GenerateDisconnectSignalMethod()
    {
        var customHandlerBuilder = new StringBuilder(1024);
        foreach (var behaviorFileContext in _behaviorFileContexts.Values)
        {
            foreach (var eventHandlerContext in behaviorFileContext.DelegateContext)
            {
                customHandlerBuilder.Append($@"        if (SignalName.{eventHandlerContext.SignalName}.Equals(signal))
        {{
            {eventHandlerContext.SignalName} -= Get{eventHandlerContext.SignalName}EventHandler();
        }}

");
            }
        }

        //lang=C#
        var myString = @$"    private void DisconnectSignal(Godot.StringName signal)
    {{
        if (Godot.GodotObject.SignalName.ScriptChanged.Equals(signal))
        {{
            ScriptChanged -= () => OnSignal(Godot.GodotObject.SignalName.ScriptChanged);
        }}

        if (Godot.GodotObject.SignalName.PropertyListChanged.Equals(signal))
        {{
            PropertyListChanged -= () => OnSignal(Godot.GodotObject.SignalName.PropertyListChanged);
        }}

        if (Godot.Node.SignalName.Ready.Equals(signal))
        {{
            Ready -= () => OnSignal(Godot.Node.SignalName.Ready);
        }}

        if (Godot.Node.SignalName.Renamed.Equals(signal))
        {{
            Renamed -= () => OnSignal(Godot.Node.SignalName.Renamed);
        }}

        if (Godot.Node.SignalName.TreeEntered.Equals(signal))
        {{
            TreeEntered -= () => OnSignal(Godot.Node.SignalName.TreeEntered);
        }}

        if (Godot.Node.SignalName.TreeExiting.Equals(signal))
        {{
            TreeExiting -= () => OnSignal(Godot.Node.SignalName.TreeExited);
        }}

        if (Godot.Node.SignalName.ChildEnteredTree.Equals(signal))
        {{
            ChildEnteredTree -= node => OnSignal(Godot.Node.SignalName.ChildEnteredTree, node);
        }}

        if (Godot.Node.SignalName.ChildExitingTree.Equals(signal))
        {{
            ChildExitingTree -= node => OnSignal(Godot.Node.SignalName.ChildExitingTree, node);
        }}
        
{customHandlerBuilder}
    }}";
        return myString;
    }

    private string GenerateStaticField()
    {
        var customSignalName2Id = new StringBuilder(1024);
        var customSignalId2Name = new StringBuilder(1024);
        var id = 10;
        foreach (var behaviorFileContext in _behaviorFileContexts.Values)
        {
            foreach (var eventHandlerContext in behaviorFileContext.DelegateContext)
            {
                customSignalName2Id.Append($"        {{ \"{eventHandlerContext.SignalName}\", {id} }},\n");
                customSignalId2Name.Append($"        {{ {id}, \"{eventHandlerContext.SignalName}\" }},\n");

                id++;
            }
        }

        customSignalName2Id.Length -= 1;
        customSignalId2Name.Length -= 1;

        var signalName2Id =
            @$"    public static readonly System.Collections.Generic.Dictionary<string, int> SignalName2Id = new()
    {{
        {{ ""script_changed"", 1 }},
        {{ ""property_list_changed"", 2 }},
        {{ ""ready"", 3 }},
        {{ ""renamed"", 4 }},
        {{ ""tree_entered"", 5 }},
        {{ ""tree_exiting"", 6 }},
        {{ ""tree_exited"", 7 }},
        {{ ""child_entered_tree"", 8 }},
        {{ ""child_exiting_tree"", 9 }},
{customSignalName2Id}
    }};";

        var signalId2Name =
            @$"    public static readonly System.Collections.Generic.Dictionary<int, string> SignalId2Name = new()
    {{
        {{ 1, ""script_changed"" }},
        {{ 2, ""property_list_changed"" }},
        {{ 3, ""ready"" }},
        {{ 4, ""renamed"" }},
        {{ 5, ""tree_entered"" }},
        {{ 6, ""tree_exiting"" }},
        {{ 7, ""tree_exited"" }},
        {{ 8, ""child_entered_tree"" }},
        {{ 9, ""child_exiting_tree"" }},
{customSignalId2Name}
    }};";

        return signalName2Id + "\n\n" + signalId2Name;
    }


    // determine the namespace the class/enum/struct is declared in, if any
    // https://andrewlock.net/creating-a-source-generator-part-5-finding-a-type-declarations-namespace-and-type-hierarchy/
    private static string GetNamespace(BaseTypeDeclarationSyntax syntax)
    {
        // If we don't have a namespace at all we'll return an empty string
        // This accounts for the "default namespace" case
        string nameSpace = string.Empty;

        // Get the containing syntax node for the type declaration
        // (could be a nested type, for example)
        SyntaxNode? potentialNamespaceParent = syntax.Parent;

        // Keep moving "out" of nested classes etc until we get to a namespace
        // or until we run out of parents
        while (potentialNamespaceParent != null &&
               potentialNamespaceParent is not NamespaceDeclarationSyntax
               && potentialNamespaceParent is not FileScopedNamespaceDeclarationSyntax)
        {
            potentialNamespaceParent = potentialNamespaceParent.Parent;
        }

        // Build up the final namespace by looping until we no longer have a namespace declaration
        if (potentialNamespaceParent is BaseNamespaceDeclarationSyntax namespaceParent)
        {
            // We have a namespace. Use that as the type
            nameSpace = namespaceParent.Name.ToString();

            // Keep moving "out" of the namespace declarations until we 
            // run out of nested namespace declarations
            while (true)
            {
                if (namespaceParent.Parent is not NamespaceDeclarationSyntax parent)
                {
                    break;
                }

                // Add the outer namespace as a prefix to the final namespace
                nameSpace = $"{namespaceParent.Name}.{nameSpace}";
                namespaceParent = parent;
            }
        }

        // return the final namespace
        return nameSpace;
    }
}