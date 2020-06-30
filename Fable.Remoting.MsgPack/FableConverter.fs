﻿module Fable.Remoting.MsgPack

open System
open System.IO
open System.Text
open System.Collections.Concurrent
open System.Collections.Generic

module Format =
    [<Literal>]
    let nil = 0xc0uy
    [<Literal>]
    let fals = 0xc2uy
    [<Literal>]
    let tru = 0xc3uy

    let inline fixposnum value = byte value
    let inline fixnegnum value = byte value ||| 0b11100000uy
    [<Literal>]
    let uint8 = 0xccuy
    [<Literal>]
    let uint16 = 0xcduy
    [<Literal>]
    let uint32 = 0xceuy
    [<Literal>]
    let uint64 = 0xcfuy

    [<Literal>]
    let int8 = 0xd0uy
    [<Literal>]
    let int16 = 0xd1uy
    [<Literal>]
    let int32 = 0xd2uy
    [<Literal>]
    let int64 = 0xd3uy

    let inline fixstr len = 160uy + byte len
    [<Literal>]
    let str8 = 0xd9uy
    [<Literal>]
    let str16 = 0xdauy
    [<Literal>]
    let str32 = 0xdbuy

    [<Literal>]
    let float32 = 0xcauy
    [<Literal>]
    let float64 = 0xcbuy

    let inline fixarr len = 144uy + byte len
    [<Literal>]
    let array16 = 0xdcuy
    [<Literal>]
    let array32 = 0xdduy

    let inline fixmap len = 128uy + byte len
    [<Literal>]
    let map16 = 0xdeuy
    [<Literal>]
    let map32 = 0xdfuy

#if !FABLE_COMPILER
let packerCache = ConcurrentDictionary<Type, obj -> Stream -> unit> ()

let inline write32bitNumber b1 b2 b3 b4 (s: Stream) =
    if b2 > 0uy || b1 > 0uy then
        s.WriteByte b1
        s.WriteByte b2
        s.WriteByte b3
    elif (b3 > 0uy) then
        s.WriteByte b3
        
    s.WriteByte b4

let inline write64bitNumber b1 b2 b3 b4 b5 b6 b7 b8 (s: Stream) =
    if b4 > 0uy || b3 > 0uy || b2 > 0uy || b1 > 0uy then
        s.WriteByte b1
        s.WriteByte b2
        s.WriteByte b3
        s.WriteByte b4
        write32bitNumber b5 b6 b7 b8 s
    else
        write32bitNumber b5 b6 b7 b8 s

let inline formatBySignedLength (length: int64) oneByte twoByte fourByte eightByte =
    if length < 128L && length > -129L then
        oneByte
    elif length < 32768L && length > -32769L then
        twoByte
    elif length < 2147483647L && length > -2147483648L then
        fourByte
    else
        eightByte   

let inline writeUnsigned32bitNumber (n: UInt32) (s: Stream) =
    write32bitNumber (n >>> 24 |> byte) (n >>> 16 |> byte) (n >>> 8 |> byte) (byte n) s

let inline writeUnsigned64bitNumber (n: UInt64) (s: Stream) =
    write64bitNumber (n >>> 56 |> byte) (n >>> 48 |> byte) (n >>> 40 |> byte) (n >>> 32 |> byte) (n >>> 24 |> byte) (n >>> 16 |> byte) (n >>> 8 |> byte) (byte n) s

let inline writeSignedNumber bytes (s: Stream) =
    if BitConverter.IsLittleEndian then
        Array.Reverse bytes

    s.Write (bytes, 0, bytes.Length)

//type MapSerializer<'k, 'v when 'k: comparison> () =
//    static member Serialize (v: obj, s: Stream) =
//        match v with
//        | :? Map<'k,'v> as mapObj ->
//            mapObj |> Map.toSeq
//        | :? Dictionary<'k,'v> as dictObj ->
//            dictObj |> Seq.map (|KeyValue|)
//        | _ -> failwith "MapSerializer input value wasn't a Map or a Dictionary"

module Write =
    let inline nil (s: Stream) = s.WriteByte Format.nil
    let inline bool x (s: Stream) = s.WriteByte (if x then Format.tru else Format.fals)

    let inline uint (n: UInt64) (s: Stream) =
        if n < 128UL then
            s.WriteByte (Format.fixposnum n)
        else
            if n < 256UL then
                s.WriteByte Format.uint8
            elif n < 65536UL then
                s.WriteByte Format.uint16
            elif n < 4294967296UL then
                s.WriteByte Format.uint32
            else
                s.WriteByte Format.uint64
            
            writeUnsigned64bitNumber n s

    let inline int (n: int64) (s: Stream) =
        if n >= 0L then
            uint (uint64 n) s 
        else
            if n > -32L then
                s.WriteByte (Format.fixnegnum n)
            else
                //todo length optimization
                s.WriteByte Format.int64
                writeSignedNumber (BitConverter.GetBytes n) s

    let inline str (str: string) (s: Stream) =
        let bytes = Encoding.UTF8.GetBytes str

        if bytes.Length < 32 then
            s.WriteByte (Format.fixstr bytes.Length)
        else
            if bytes.Length < 256 then
                s.WriteByte Format.str8
            elif bytes.Length < 65536 then
                s.WriteByte Format.str16
            else
                s.WriteByte Format.str32

            writeUnsigned32bitNumber (uint32 bytes.Length) s

        s.Write (bytes, 0, bytes.Length)

    let inline float32 (n: float32) (s: Stream) =
        s.WriteByte Format.float32
        writeSignedNumber (BitConverter.GetBytes n) s
        
    let inline float64 (n: float) (s: Stream) =
        s.WriteByte Format.float64
        writeSignedNumber (BitConverter.GetBytes n) s

    let rec array (s: Stream) (arr: Array) =
        if arr.Length < 16 then
            s.WriteByte (Format.fixarr arr.Length)
        elif arr.Length < 65536 then
            s.WriteByte Format.array16
            writeUnsigned32bitNumber (uint32 arr.Length) s
        else
            s.WriteByte Format.array32
            writeUnsigned32bitNumber (uint32 arr.Length) s

        for x in arr do
            writeObj x s

    and inline tuple (s: Stream) (items: obj[]) =
        array s items

    and union (s: Stream) tag (vals: obj[]) =
        s.WriteByte (Format.fixarr 2uy)
        s.WriteByte (Format.fixposnum tag)
        array s vals

    //and map<'a, 'b> (dict: IDictionary<'a, 'b>) (s: Stream) =
    //    if dict.Count < 16 then
    //        s.WriteByte (Format.fixmap dict.Count)
    //    elif dict.Count < 65536 then
    //        s.WriteByte Format.map16
    //        writeUnsignedNumber (uint64 dict.Count) s
    //    else
    //        s.WriteByte Format.map32
    //        writeUnsignedNumber (uint64 dict.Count) s

    //    for kvp in dict do
    //       writeObj kvp.Key s
    //       writeObj kvp.Value s

    and writeObj (x: obj) (s: Stream) =
        if isNull x then nil s else

        let t = x.GetType()

        match packerCache.TryGetValue (if t.IsArray then typeof<Array> else t) with
        | (true, writer) ->
            writer x s
        | _ ->
            if FSharp.Reflection.FSharpType.IsRecord t then
                let props = FSharp.Reflection.FSharpType.GetRecordFields t |> Array.sortBy (fun x -> x.Name)

                let writer x (s: Stream) =
                    props
                    |> Array.map (fun prop -> prop.GetValue x)
                    |> array s

                packerCache.TryAdd (t, writer) |> ignore
                writer x s
            elif FSharp.Reflection.FSharpType.IsUnion t then
                //todo optimization: if a case has a single parameter, don't emit an array

                let writer x (s: Stream) =
                    let case, vals = FSharp.Reflection.FSharpValue.GetUnionFields (x, t)
                    union s case.Tag vals

                packerCache.TryAdd (t, writer) |> ignore
                writer x s
            elif FSharp.Reflection.FSharpType.IsTuple t then
                let writer x (s: Stream) =
                    FSharp.Reflection.FSharpValue.GetTupleFields x |> tuple s

                packerCache.TryAdd (t, writer) |> ignore
                writer x s
            else
                failwithf "Cannot pack %s" t.Name

packerCache.TryAdd (typeof<unit>, fun _ s -> Write.nil s) |> ignore
packerCache.TryAdd (typeof<bool>, fun x s -> Write.bool (x :?> bool) s) |> ignore
packerCache.TryAdd (typeof<string>, fun x s -> Write.str (x :?> string) s) |> ignore
packerCache.TryAdd (typeof<int>, fun x s -> Write.int (x :?> int |> int64) s) |> ignore
packerCache.TryAdd (typeof<int16>, fun x s -> Write.int (x :?> int16 |> int64) s) |> ignore
packerCache.TryAdd (typeof<int64>, fun x s -> Write.int (x :?> int64) s) |> ignore
packerCache.TryAdd (typeof<UInt32>, fun x s -> Write.uint (x :?> UInt32 |> uint64) s) |> ignore
packerCache.TryAdd (typeof<UInt16>, fun x s -> Write.uint (x :?> UInt16 |> uint64) s) |> ignore
packerCache.TryAdd (typeof<UInt64>, fun x s -> Write.uint (x :?> UInt64) s) |> ignore
packerCache.TryAdd (typeof<float32>, fun x s -> Write.float32 (x :?> float32) s) |> ignore
packerCache.TryAdd (typeof<float>, fun x s -> Write.float64 (x :?> float) s) |> ignore
packerCache.TryAdd (typeof<Array>, fun x s -> Write.array s (x :?> Array)) |> ignore
packerCache.TryAdd (typeof<DateTime>, fun x s -> Write.int (x :?> DateTime).Ticks s) |> ignore
//todo timezone info
//packerCache.TryAdd (typeof<DateTimeOffset>, fun x s -> Write.int (x :?> DateTimeOffset).Ticks s) |> ignore

#endif

module Read =
    let inline flip (data: byte[]) pos len =
        let arr = Array.zeroCreate len

        for i in 0 .. len - 1 do
            arr.[i] <- data.[pos + len - 1 - i]

        arr

    let inline interpretNumAs typ n =
        if typ = typeof<Int32> then int32 n |> box
        elif typ = typeof<Int64> then int64 n |> box
        elif typ = typeof<Int16> then int16 n |> box
        elif typ = typeof<UInt32> then uint32 n |> box
        elif typ = typeof<UInt64> then uint64 n |> box
        elif typ = typeof<UInt16> then uint16 n |> box
        elif typ = typeof<DateTime> then DateTime (int64 n) |> box
        else failwithf "Cannot interpret number %A as %s" n typ.Name

    type Reader (data: byte[]) =
        let mutable pos = 0

        let readInt len m =
            if BitConverter.IsLittleEndian then
                let flipped = flip data pos len
                let x = m (flipped, 0)
                pos <- pos + len
                x
            else
                pos <- pos + len
                m (data, pos - len)

        member _.ReadByte () =
            pos <- pos + 1
            data.[pos - 1]

        member _.ReadString len =
            pos <- pos + len
            Encoding.UTF8.GetString (data, pos - len, len)

        member _.ReadUInt16 () =
            readInt 2 BitConverter.ToUInt16

        member _.ReadInt16 () =
            readInt 2 BitConverter.ToInt16

        member _.ReadUInt32 () =
            readInt 4 BitConverter.ToUInt32

        member _.ReadInt32 () =
            readInt 4 BitConverter.ToInt32

        member _.ReadUInt64 () =
            readInt 8 BitConverter.ToUInt64

        member _.ReadInt64 () =
            readInt 8 BitConverter.ToInt16

        member _.ReadFloat32 () =
            readInt 4 BitConverter.ToSingle

        member _.ReadFloat64 () =
            readInt 8 BitConverter.ToDouble

        member x.ReadArray len elementType =
            let getLength () =
                let b = x.ReadByte ()

                match b with
                | Format.array16 ->
                    x.ReadUInt16 () |> int
                | Format.array32 ->
                    x.ReadUInt32 () |> int
                | _ ->
                    failwithf "Expected array length format, got %d" b
                    
            let len = Option.defaultWith getLength len
            
#if !FABLE_COMPILER
            let arr = Array.CreateInstance (elementType, len)

            for i in 0 .. len - 1 do
                arr.SetValue (x.Read elementType, i)

            arr
#else
            [|
                for i in 0 .. len - 1 ->
                    x.Read elementType
            |]
#endif

        member x.Read (t, b) =
            match b with
            | Format.str8 -> x.ReadByte () |> int |> x.ReadString |> box
            | Format.str16 -> x.ReadUInt16 () |> int |> x.ReadString |> box
            | Format.str32 -> x.ReadUInt32 () |> int |> x.ReadString |> box
            | Format.int64 -> x.ReadInt64 () |> interpretNumAs t
            | Format.int32 -> x.ReadInt32 () |> interpretNumAs t
            | Format.int16 -> x.ReadInt16 () |> interpretNumAs t
            | Format.uint16 -> x.ReadUInt16 () |> interpretNumAs t
            | Format.uint32 -> x.ReadUInt32 () |> interpretNumAs t
            | Format.uint64 -> x.ReadUInt64 () |> interpretNumAs t
            | Format.float32 -> x.ReadFloat32 () |> box
            | Format.float64 -> x.ReadFloat64 () |> box
            | Format.nil -> box null
            | Format.tru -> box true
            | Format.fals -> box false
            // fixarr
            | b when b >>> 4 = 0b00001001uy ->
                if Reflection.FSharpType.IsRecord t then
                    let props = FSharp.Reflection.FSharpType.GetRecordFields t |> Array.sortBy (fun x -> x.Name)
                    Reflection.FSharpValue.MakeRecord (t, props |> Array.map (fun prop -> x.Read prop.PropertyType))
                else
                    let len = b &&& 0b00001111uy |> int

                    if Reflection.FSharpType.IsUnion t then
                        let tag = x.Read typeof<int> :?> int
                        // don't care about this byte, it's going to be a fixarr of length fields.Length
                        x.ReadByte () |> ignore

                        let case = Reflection.FSharpType.GetUnionCases t |> Array.find (fun y -> y.Tag = tag)
                        let fields = case.GetFields ()
                        Reflection.FSharpValue.MakeUnion (case, fields |> Array.map (fun y -> x.Read y.PropertyType))
#if FABLE_COMPILER // Fable does not recognize Option as a union
                    elif t.IsGenericType && t.GetGenericTypeDefinition () = typedefof<Option<_>> then
                        // don't care about this byte, it's going to be a fixarr of length 2
                        x.ReadByte () |> ignore

                        let tag = x.ReadByte ()

                        // none case
                        if tag = 0uy then
                            x.ReadByte () |> ignore
                            box null
                        else
                            x.Read (t.GetGenericArguments () |> Array.head)
#endif
                    elif Reflection.FSharpType.IsTuple t then
                        // don't care about this byte, it's going to be an fixarr of length fields.Length
                        x.ReadByte () |> ignore
                        Reflection.FSharpValue.MakeTuple (Reflection.FSharpType.GetTupleElements t |> Array.map x.Read, t)
                    elif t.IsArray then
                        x.ReadArray (Some len) (t.GetElementType()) |> box
                    else
                        failwithf "Expecting %s, but the data contains a fixarr." t.Name
            // fixposnum
            | b when b >>> 7 = 0b00000000uy ->
                interpretNumAs t b
            // fixnegnum
            | b when b >>> 5 = 0b00000111uy ->
                sbyte b |> interpretNumAs t
            // fixstr
            | b when b >>> 5 = 0b00000101uy ->
                let len = b &&& 0b00011111uy |> int
                x.ReadString len |> box
            | _ ->
                failwithf "Byte %d, expected type %s." b t.Name

        member x.Read t =
            let b = x.ReadByte ()

            //// todo cache
            //if t.IsGenericType && t.GetGenericTypeDefinition () = typedefof<Option<_>> then
            //    let value = x.Read (t.GetGenericArguments () |> Array.head, b)

            //    let optionCases = Reflection.FSharpType.GetUnionCases t
            //    if isNull value then
            //        Reflection.FSharpValue.MakeUnion (optionCases.[0], [||])
            //    else
            //        Reflection.FSharpValue.MakeUnion (optionCases.[1], [| value |])
            //else
            x.Read (t, b)
            