﻿module Fungible.Simple

open Fungible.Core
open Fungible.Attributes

open System
open System.Collections.Generic
open System.Linq
open System.Linq.Expressions
open System.ComponentModel

open FSharp
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Reflection

/// <summary>
/// The definition of an F# cleaner function 
/// </summary>
[<CLIMutable>]
type TransformDefinition = 
    {
        /// The path to the target field for this cleaner
        [<Description("The path to the target field for this cleaner")>]
        TargetPath: string
        /// The name of the function which will be performed on the target field
        [<Description("The name of the function which will be performed on the target field")>]
        FunctionName: string
        /// The arguments to pass to the function
        [<Description("Arguments to be passed to the operation")>]
        FunctionArgs: string []
    }
    with override t.ToString () = sprintf "%s <- %s with %A" t.TargetPath t.FunctionName t.FunctionArgs

/// Semi-internal machinery used for figuring out information about input types
module Types = 

    /// Used to describe how a transform interacts with data
    type TypeKind =
        | Inner
        | Outter
        | Fixed of Type

    /// A description of a transform in terms of its types and needed wrappers
    type FunctionKind =
        {
            ExprWrapper: Expr -> FieldAction
            InputKind: TypeKind
            OutputKind: TypeKind
        }

    /// Based on the kind of transform this function tells the machinery which types to look at
    let getFunctionKind (funcType: string) = 
        match funcType.ToLowerInvariant() with
        | "map" ->      Map,        Inner,                  Inner
        | "filter" ->   Filter,     Inner,                  Fixed typeof<bool>
        | "collect" ->  Collect,    Inner,                  Outter
        | "default" ->  Default,    Fixed typeof<unit>,     Outter
        | "function" -> Function,   Outter,                 Outter
        | "add" ->      Add,        Fixed typeof<unit>,     Outter
        | otherwise -> failwithf "Unexpected type in data cleaner: %s" otherwise     
        |> fun (ew, ik, ok) -> { ExprWrapper = ew; InputKind = ik; OutputKind = ok }

    open CollectionHelpers
    
    /// Gets the inner type(s) of a collection
    let getInnerType (targetType: Type) = 
        match targetType with
        | IsMapType t -> FSharpType.MakeTupleType (t.GetGenericArguments())
        | IsOptionType t -> t.GetGenericArguments().[0] 
        | t when t.IsArray -> t.GetElementType()
        | t -> t    

    /// Gets the actual type used for wrapping the transform input
    let getActualType (targetType: Type) (tk: TypeKind) =
        match tk with
        | Inner -> getInnerType targetType 
        | Outter -> targetType
        | Fixed t -> t

    /// Converts a standard .NET long form name into a path
    let nameToPath (name: string) = 
        name.Split([|'.'|], StringSplitOptions.None) |> Array.toList |> List.rev

module internal Internals = 
    open Types

    let convertFromArgsToInputType (t: Type) (args: string []) =
        match t with
        | t when t = typeof<string[]> -> args |> box
        | t when t = typeof<char[]> -> args |> Array.map (fun arg -> char arg) |> box
        | _ -> failwithf "Unable to convert to basic cleaner input type: %s" (t.FullName)

    let generateBasicCleaner (fmod: Type) (targetType: Type) (funcName: string) (funcArg: string []) =
        let mi = fmod.GetMethod(funcName)
        let prms = mi.GetParameters()

        if prms.Length > 2 then
            failwithf "Error while resolving basic cleaner function: %s, too many parameters found." funcName

        if prms.Length = 1 && funcArg.Length > 0 then
            failwithf "Basic data cleaning function %s does not support arguments." funcName 

        let funcType = 
            match getFunctionType(mi) with
            | Some v -> v
            | None -> failwithf "Basic data cleaning not supported with %s" funcName 

        let functionKind = getFunctionKind funcType.Type

        let inType = getActualType targetType functionKind.InputKind
        //let outType = getActualType targetType functionKind.OutputKind

        if prms.Length = 1 then
            let arg = Var("x", inType, false)
            let useArg = Expr.Var(arg)

            let funcExpr = Expr.Call(mi, [useArg])
            Expr.Lambda(arg, funcExpr) |> functionKind.ExprWrapper
        else
            let arg = Var("x", inType, false)
            let useArg = Expr.Var(arg)
            let dcFuncArgs =  
                try 
                    let argsPrm = mi.GetParameters() |> Array.map (fun pi -> pi.ParameterType)
                    if argsPrm.Length <> 2 then failwith "Function has an incorrect number of parameters." funcName
                    let convertedArgs = convertFromArgsToInputType argsPrm.[0] funcArg
                    Expr.Coerce(<@@ convertedArgs @@>, argsPrm.[0])
                with ex -> failwithf "An error occured while creating a basic cleaner with function (%s) and arguments (%A): %s" funcName funcArg ex.Message

            let funcExpr = Expr.Call(mi, [ dcFuncArgs; useArg ])
            Expr.Lambda(arg, funcExpr) |> functionKind.ExprWrapper

open Internals
open Types

type Transform = string list * FieldAction

/// <summary>
/// Creates a transform out of an F# function as defined in a TransformDefinition
/// </summary>
/// <param name="functionModule">The module in which the function lives</param>
/// <param name="propertyMap">A map of paths to types gotten from getPathsAndTypes<'t></param>
/// <param name="basic">A definition of the transform to be used</param>
let generateTransform<'U> (functionModule: Type) (propertyMap: Map<string list, Type>) (basic: TransformDefinition) : Transform =
    let path = nameToPath basic.TargetPath 
    let propertType = propertyMap.[path]
    let cleaner = generateBasicCleaner functionModule propertType basic.FunctionName basic.FunctionArgs
    path, cleaner

/// <summary>
/// Takes a set of transforms and compiles them into a function that does all of the given transforms to the data tree
/// </summary>
/// <param name="transforms">The sequence of transforms as applied to the data.</param>
let compileTransforms<'U, 'T> (transforms: Transform seq) =
    let recordCloningSettings = { CloneWhenNoChanges = false; FailOnUnsupportedType = true }

    let compiledCleaners = 
        transforms
        |> Seq.groupBy fst
        |> Seq.map (fun (sl, slfa) -> sl, slfa |> Seq.map snd |> Seq.toList)
        |> Map.ofSeq    
    genrateRecordTransformFunctionWithArgs<'U,'T> recordCloningSettings "replaceMe" compiledCleaners
