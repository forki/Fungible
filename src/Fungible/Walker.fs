﻿module Fungible.Walker

open Fungible.Helpers
open Fungible.Helpers.ExprHelpers
open Fungible.Helpers.CollectionHelpers
open Fungible.Helpers.Linq

open System
open System.Reflection
open System.Linq.Expressions

open Microsoft.FSharp.Reflection
open Microsoft.FSharp.Quotations

type WalkerSettings = 
    {
        CompareCSharpProperties: bool
    }
    static member Default = { CompareCSharpProperties = true }

type InputFunction = obj -> obj -> string list -> unit

let compareArrays (a1: obj []) (a2: obj []) =   
    let mutable idx1 = 0
    let mutable idx2 = 0
    while idx1 < a1.Length && idx2 < a2.Length do
        // f()
    ()

let getTypedException (utype: Type) (message: string) =
    let m = (getMethod <@ failwith message @>).MakeGenericMethod([|utype|])
    Expr.Call(m, [ <@ message @> ])

let sequenceExprs (expr1: Expr) (expr2: Expr) =
    Expr.Sequential(expr1, expr2)

let sequenceLambda (t1: Type) (t2: Type)=
    let instance1Var = Var("instance1", t1, false)
    let instance1Arg = Expr.Var(instance1Var)
    let instance2Var = Var("instance2", t2, false)
    let instance2Arg = Expr.Var(instance2Var)
    Expr.Lambda(instance1Var, Expr.Lambda(instance2Var, sequenceExprs instance1Arg instance2Arg))

let genFieldWalker (instance1: Expr) (instance2: Expr) (field: PropertyInfo) (path: string list) dispatchOnType : Expr = 
    let p1val = Expr.PropertyGet(instance1, field) 
    let p2val = Expr.PropertyGet(instance2, field)
    dispatchOnType field.PropertyType path p1val p2val

let genRecordWalker (rtype: Type) (path: string list) (instance1: Expr) (instance2: Expr) dispatchOnType : Expr =  
    FSharpType.GetRecordFields(rtype) 
    |> Array.toList
    |> List.map (fun field -> field.Name :: path, field)
    |> List.map (fun (fpath, field) -> genFieldWalker instance1 instance2 field fpath dispatchOnType)
    |> List.reduce (fun e1 e2 ->  sequenceExprs e1 e2) 

let genPOCOWalker (ctype: Type) (path: string list) (instance1: Expr) (instance2: Expr) dispatchOnType : Expr =
    ctype.GetProperties()    
    |> Array.toList
    |> List.filter (fun pi -> pi.CanRead) // Filter out Set-Only Properties
    |> List.map (fun field -> field.Name :: path, field)
    |> List.map (fun (fpath, field) -> genFieldWalker instance1 instance2 field fpath dispatchOnType)
    |> List.reduce (fun e1 e2 ->  sequenceExprs e1 e2) 

// NOTE: Will only walk if the union cases are the same
let genUnionWalker (utype: Type) (path: string list) (instance1: Expr) (instance2: Expr) dispatchOnType : Expr =
    let genCaseTest case =
        let test1 = Expr.UnionCaseTest(instance1, case)
        let test2 = Expr.UnionCaseTest(instance2, case)
        <@@ (%%test1) && (%%test2) @@>

    let genIf ifCase thenCase elseCase = Expr.IfThenElse(ifCase, thenCase, elseCase) 

    let makeCaseWalker (ci: UnionCaseInfo) = 
        ci.GetFields()
        |> Array.map (fun field -> genFieldWalker instance1 instance2 field (field.Name :: path) dispatchOnType)
        |> Array.reduce (fun e1 e2 -> sequenceExprs e1 e2)  

    let doNothing() = ()

    FSharpType.GetUnionCases utype  
    |> Array.map (fun case -> genIf (genCaseTest case) (makeCaseWalker case))
    |> Array.foldBack (fun iff st -> iff st) <| <@@ doNothing() @@>

let makeFuncionCall (fexpr: Expr) (path: string list) (instance1: Expr) (instance2: Expr) =
    let objExpr1 = Expr.Coerce(instance1, typeof<obj>)
    let objExpr2 = Expr.Coerce(instance2, typeof<obj>)
    <@@ (%%fexpr : InputFunction) %%objExpr1 %%objExpr2 path @@>

let callFunAndCont (fexpr: Expr) (path: string list) (instance1: Expr) (instance2: Expr) (next: Expr) : Expr = 
    let functionExpr = makeFuncionCall fexpr path instance1 instance2    
    sequenceExprs functionExpr next

let rec dispatchOnType (settings: WalkerSettings) (fexpr: Expr) (mtype: Type) (path: string list) (instance1: Expr) (instance2: Expr) : Expr = 
    match mtype with 
    | _ when FSharpType.IsUnion mtype ->
                let walker = genUnionWalker mtype path instance1 instance2 (dispatchOnType settings fexpr)
                callFunAndCont fexpr path instance1 instance2 walker
    | _ when FSharpType.IsRecord mtype -> 
                let walker = genRecordWalker mtype path instance1 instance2 (dispatchOnType settings fexpr)
                callFunAndCont fexpr path instance1 instance2 walker
    | _ when mtype.IsValueType || mtype = typeof<String> -> makeFuncionCall fexpr path instance1 instance2     
    | _ when mtype.IsClass && settings.CompareCSharpProperties -> 
                let walker = genPOCOWalker mtype path instance1 instance2 (dispatchOnType settings fexpr)
                callFunAndCont fexpr path instance1 instance2 walker
    | _ -> failwithf "Unexpected type: %s" (mtype.ToString())

let makeWalkerLambdaExpr (settings: WalkerSettings) (ftype:Type) (mtype: Type) (path: string list) : Expr =
    let argf = Var("f", ftype, false)
    let useArgF = Expr.Var(argf)
    let arg1 = Var("x", mtype, false)
    let useArg1 = Expr.Var(arg1)
    let arg2 = Var("y", mtype, false)
    let useArg2 = Expr.Var(arg2)
    let contents = dispatchOnType settings useArgF mtype path useArg1 useArg2
    Expr.Lambda(argf, Expr.Lambda(arg1, Expr.Lambda(arg2, contents)))

open FSharp.Quotations.Evaluator

let generateWalker<'T> (settings: WalkerSettings) : (InputFunction -> 'T -> 'T -> unit) =
    let baseType = typeof<'T>
    let funType = typeof<InputFunction>
    let contents = makeWalkerLambdaExpr settings funType baseType []
    let castExpr : Expr<InputFunction -> 'T -> 'T -> unit> = contents |> Expr.Cast
    castExpr.Compile()

let testLambda =
    let farg = Var("f", typeof<int -> string>, false)
    let useFarg = Expr.Var(farg)
    Expr.Lambda(farg, <@@ (%%useFarg) 1 : string @@>)

