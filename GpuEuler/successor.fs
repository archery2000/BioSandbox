﻿module GpuEuler

open Graphs
open Alea.CUDA
open Alea.CUDA.Unbound
open Alea.CUDA.Utilities
open GpuSimpleSort

let blockSize = DirectedGraph.blockSize
let worker = DirectedGraph.worker
let target = DirectedGraph.target

[<Kernel; ReflectedDefinition>]    
let scatter (start : deviceptr<int>) (end' : deviceptr<int>) (len: int) (falsesScan : deviceptr<int>) (revBits : deviceptr<int>) (outStart : deviceptr<int>) (outEnd : deviceptr<int>) =
    let idx = blockIdx.x * blockDim.x + threadIdx.x
    if idx < len then

        let totalFalses = falsesScan.[len - 1] + revBits.[len - 1]

        // when the bit is equal to 1 - it will be offset by the scan value + totalFalses
        // if it's equal to 0 - just the scan value contains the right address
        let addr = if revBits.[idx] = 1 then falsesScan.[idx] else totalFalses + idx - falsesScan.[idx]
        outStart.[addr] <- start.[idx]
        outEnd.[addr] <- end'.[idx]

let getEdgesGpu (gr : DirectedGraph<'a>) =
    let rowIndex = gr.RowIndex
    let colIndex = gr.ColIndex
    let len = rowIndex.Length

    let lp = LaunchParam(divup len blockSize, blockSize)

    let dStart = worker.Malloc<int>(colIndex.Length)
    let dEnd = worker.Malloc<int>(colIndex.Length)
    let dRowIndex = worker.Malloc(rowIndex)
    let dColIndex = worker.Malloc(colIndex)

    worker.Launch <@ DirectedGraph.toEdgesKernel @> lp dRowIndex.Ptr len dColIndex.Ptr dStart.Ptr dEnd.Ptr

    dStart, dEnd

let sortStartEnd (dStart : DeviceMemory<int>) (dEnd : DeviceMemory<int>) len =
    let lp = LaunchParam(divup len blockSize, blockSize)

    use reduceModule = new DeviceReduceModule<int>(target, <@ max @>)
    use reducer = reduceModule.Create(len)

    use scanModule = new DeviceScanModule<int>(target, <@ (+) @>)
    use scanner = scanModule.Create(len)

    use dBits = worker.Malloc(len)
    use numFalses = worker.Malloc(len)
    use dStartTemp = worker.Malloc(len)
    use dEndTemp = worker.Malloc(len)

    // Number of iterations = bit count of the maximum number
    let numIter = reducer.Reduce(dEnd.Ptr, len) |> getBitCount

    let getArr i = if i &&& 1 = 0 then dStart, dEnd else dStartTemp, dEndTemp
    let getOutArr i = getArr (i + 1)

    for i = 0 to numIter - 1 do
        // compute significant bits
        let start, end' = getArr i
        let outStart, outEnd = getOutArr i
        worker.Launch <@ getNthSignificantReversedBit @> lp end'.Ptr i len dBits.Ptr

        // scan the bits to compute starting positions further down
        scanner.ExclusiveScan(dBits.Ptr, numFalses.Ptr, 0, len)

        // scatter
        worker.Launch <@ scatter @> lp start.Ptr end'.Ptr len numFalses.Ptr dBits.Ptr outStart.Ptr outEnd.Ptr
    
    let outStart, outEnd = getOutArr (numIter - 1)
    outStart.Gather(), outEnd.Gather()        