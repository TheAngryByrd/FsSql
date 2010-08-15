﻿module FsSql

open System
open System.Data
open System.Data.SqlClient
open System.Linq
open System.Text.RegularExpressions
open Microsoft.FSharp.Reflection
open FsSqlImpl

let log s = printfn "%A: %s" DateTime.Now s
let logf a = sprintf a >> log

module Seq =
    let ofDataReader (dr: IDataReader) =
        log "started ofDataReader"
        let lockObj = obj()
        let lockReader f = lock lockObj f
        let read()() =            
            let h = dr.Read()
            //logf "read record %A" dr.["id"]
            if h
                then DictDataRecord(dr) :> IDataRecord
                else null
        let lockedRead = lockReader read
        let records = Seq.initInfinite (fun _ -> read()())
                      |> Seq.takeWhile (fun r -> r <> null)
        seq {
            try
                yield! records
            finally
                log "datareader dispose"
                dr.Dispose()}
    
let PrintfFormatProc (worker: string * obj list -> 'd)  (query: PrintfFormat<'a, _, _, 'd>) : 'a =
    if not (FSharpType.IsFunction typeof<'a>) then
        unbox (worker (query.Value, []))
    else
        let rec getFlattenedFunctionElements (functionType: Type) =
            let domain, range = FSharpType.GetFunctionElements functionType
            if not (FSharpType.IsFunction range)
                then domain::[range]
                else domain::getFlattenedFunctionElements(range)
        let types = getFlattenedFunctionElements typeof<'a>
        let rec proc (types: Type list) (values: obj list) (a: obj) : obj =
            let values = a::values
            match types with
            | [x;_] -> 
                let result = worker (query.Value, List.rev values)
                box result
            | x::y::z::xs -> 
                let cont = proc (y::z::xs) values
                let ft = FSharpType.MakeFunctionType(y,z)
                let cont = FSharpValue.MakeFunction(ft, cont)
                box cont
            | _ -> failwith "shouldn't happen"
        
        let handler = proc types []
        unbox (FSharpValue.MakeFunction(typeof<'a>, handler))

let sqlProcessor (conn: #IDbConnection) (sql: string, values: obj list) =
    let stripFormatting s =
        let i = ref -1
        let eval (rxMatch: Match) =
            incr i
            sprintf "@p%d" !i
        Regex.Replace(s, "%.", eval)
    let sql = stripFormatting sql
    let cmd = conn.CreateCommand()
    cmd.CommandText <- sql
    let createParam i (p: obj) =
        let param = cmd.CreateParameter()
        //param.DbType <- DbType.
        param.ParameterName <- sprintf "@p%d" i
        param.Value <- p
        cmd.Parameters.Add param |> ignore
    values |> Seq.iteri createParam
    cmd

let sqlProcessorToDataReader a b = 
    let cmd = sqlProcessor a b
    cmd.ExecuteReader()

let sqlProcessorToUnit a b =
    let cmd = sqlProcessor a b
    cmd.ExecuteNonQuery() |> ignore

let execReader connectionFactory a = PrintfFormatProc (sqlProcessorToDataReader connectionFactory) a
let execNonQuery connectionFactory a = PrintfFormatProc (sqlProcessorToUnit connectionFactory) a

let transactionalWithIsolation (isolation: IsolationLevel) (conn: #IDbConnection) (f: #IDbConnection -> 'a -> 'b) (a: 'a) =
    let tx = conn.BeginTransaction(isolation)
    try
        let r = f conn a
        tx.Commit()
        r
    with e ->
        tx.Rollback()
        reraise()

let transactional a = 
    transactionalWithIsolation IsolationLevel.Unspecified a

type TxResult<'a> = Success of 'a | Failure of exn

let transactional2 (conn: #IDbConnection) (f: #IDbConnection -> 'a -> 'b) (a: 'a) =
    let tx = conn.BeginTransaction()
    try
        let r = f conn a
        tx.Commit()
        Success r
    with e ->
        tx.Rollback()
        Failure e

let mapCount (dr: #IDataReader) =
    try
        if not (dr.Read())
            then failwith "No results"
            else dr.GetInt64(0)
    finally
        dr.Dispose()

let readField (field: string) (record: #IDataRecord) : 'a option =
    let o = record.[field]
    if o = upcast DBNull.Value
        then None
        else Some (unbox o)

let readInt : string -> #IDataRecord -> int option = readField 

let readString : string -> #IDataRecord -> string option = readField 

let writeOption = 
    function
    | None -> DBNull.Value :> obj
    | Some x -> box x

let findOne mapper query id =
    let r = query id
            |> Seq.ofDataReader
            |> Seq.map mapper
            |> Seq.toList
    if r.Length = 0
        then None
        else Some r.[0]

let getOne mapper query id =
    query id
    |> Seq.ofDataReader
    |> Seq.map mapper
    |> Enumerable.Single

let fields t =
    t.GetType()
    |> FSharpType.GetRecordFields
    |> Seq.map (fun p -> p.Name)

let fieldList t =
    String.Join(",", fields t |> Seq.toArray)
