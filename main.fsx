open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open System.IO
open System.IO.Compression

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
    |> List.filter (fun (l, _) -> l <= level)
    |> List.map snd

type Spelling = {
    IsCommon: bool
    Text: string
}

type Word = {
    Id: string
    Spellings: Spelling list
}

let parseSpelling (json: JsonElement) =
    json.EnumerateArray()
    |> Seq.toList
    |> List.map(fun spelling -> {
        IsCommon = spelling.GetProperty("common").GetBoolean()
        Text = spelling.GetProperty("text").GetString()
    })

let parseWord (json: JsonElement) =
    {
        Id = json.GetProperty("id").GetString()
        Spellings = parseSpelling (json.GetProperty("kanji"))
    }

let loadDict () =
    async {
        use zip = ZipFile.OpenRead("jmdict-eng-3.1.0+20200915122254.json.zip")
        use stream = zip.Entries.[0].Open()
        use sr = new StreamReader(stream)
        let! text = sr.ReadToEndAsync() |> Async.AwaitTask
        let json = JsonDocument.Parse(text)
        let words =
            json.RootElement.GetProperty("words").EnumerateArray()
            |> Seq.toList
            |> List.map parseWord
            |> List.filter (fun word -> word.Spellings.Length > 0)
        return words
    }

let main =
    async {
        let! allKanji = getKanji ()
        let knownKanji = getKanjiByLevel allKanji level
        let! dict = loadDict ()
        printfn "%A" dict
        return ()
    }

Async.RunSynchronously main
