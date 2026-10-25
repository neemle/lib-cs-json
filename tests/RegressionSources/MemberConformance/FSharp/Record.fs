namespace Neemle.DBAL.JsonMemberConformance
[<Struct>]
type RealRecord = { Value : int }

type RealUnion = CaseA | CaseB of int
[<Struct>]
type RealStructUnion = StructA | StructB of int
type RealObject(value : int) =
    member _.Value = value
