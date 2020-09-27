open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json

let args = Environment.GetCommandLineArgs()

let level =
    try
        Int32.Parse args.[2]
    with
    | _ -> failwith "Please pass the level as an argument."

let wanikaniApiKey = Environment.GetEnvironmentVariable("WANIKANI_API_KEY")
if String.IsNullOrEmpty wanikaniApiKey then
    failwith "Please define the WANIKANI_API_KEY environment variable."

let hc = new HttpClient()
hc.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", wanikaniApiKey)

let url = "https://api.wanikani.com/v2/subjects?types=kanji"

let makeRequest (url: string) = async {
    let! resp = hc.GetAsync(url) |> Async.AwaitTask
    resp.EnsureSuccessStatusCode() |> ignore
    let! content = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
    let j = JsonDocument.Parse(content)
    return j.RootElement
}

let rec depaginate (acc) (j: JsonElement) =
    async {
        let nextUrl = j.GetProperty("pages").GetProperty("next_url").GetString()
        if isNull nextUrl then
            return acc
        else
            let data = j.GetProperty("data")
            let! nextJ = makeRequest nextUrl
            return! depaginate (data :: acc) nextJ
    }

let getKanji () =
    async {
        let! j = makeRequest url
        let! js = depaginate [] j
        let kanji = 
            [
                for data in js do
                    for k in data.EnumerateArray() do
                        yield k.GetProperty("data").GetProperty("level").GetInt32(), k.GetProperty("data").GetProperty("characters").GetString()
            ]
        return kanji
    }

let getKanjiByLevel (kanjiList: (int * string) list) (level: int) =
    kanjiList
    |> List.filter (fun (l, c) -> l <= level)
    |> List.map snd

let main =
    async {
        let! allKanji = getKanji ()
        let knownKanji = getKanjiByLevel allKanji level
        for kanji in knownKanji do
            printfn "%s" kanji
        return ()
    }

Async.RunSynchronously main
