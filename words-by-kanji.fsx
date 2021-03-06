open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open System.IO
open System.IO.Compression

module Jmdict =
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

    let isKanji (c: char) =
        c >= '\u4E00' && c <= '\u9FAF'

    let parseWord (json: JsonElement) =
        {
            Id = json.GetProperty("id").GetString()
            Spellings =
                json.GetProperty("kanji")
                |> parseSpelling
                |> List.filter (fun w -> Seq.exists isKanji w.Text)
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

    let filterSpelling (knownKanji: char Set) (spelling: Spelling) =
        let spellingKanji =
            spelling.Text.ToCharArray()
            |> Array.filter isKanji
            |> set

        spellingKanji.IsSubsetOf knownKanji

    let chooseWord f (word: Word) =
        match List.filter f word.Spellings with
        | [] -> None
        | s -> Some { word with Spellings = s }

    let getLearnableWords dict knownKanji =
        let allWords =
            List.choose (chooseWord (fun s -> filterSpelling knownKanji s)) dict
            |> List.distinctBy (fun w -> w.Spellings)
        let commonWords =
            List.choose (chooseWord (fun s -> s.IsCommon)) allWords
            |> List.distinctBy (fun w -> w.Spellings)
        allWords, commonWords

    let wordToString (word: Word) =
        word.Spellings
        |> List.map (fun s -> s.Text)
        |> List.reduce (sprintf "%s, %s")

module WaniKani =
    let private wanikaniApiKey = Environment.GetEnvironmentVariable("WANIKANI_API_KEY")
    if String.IsNullOrEmpty wanikaniApiKey then
        failwith "Please define the WANIKANI_API_KEY environment variable."

    let private hc = new HttpClient()
    hc.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", wanikaniApiKey)
    hc.Timeout <- TimeSpan.FromSeconds 10.

    let private depaginate (fetch: string -> Async<'T>) (next: 'T -> string option) url =
        let rec loop acc url =
            async {
                let! page = fetch url
                let nextUrl = next page
                match nextUrl with
                | None -> return acc
                | Some u -> return! loop (page :: acc) u
            }
        loop [] url

    let private getPage (url: string) = async {
        let! resp = hc.GetAsync(url) |> Async.AwaitTask
        resp.EnsureSuccessStatusCode() |> ignore
        let! content = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
        let j = JsonDocument.Parse(content)
        return j.RootElement
    }

    let private getNextUrl (j: JsonElement) =
        match j.GetProperty("pages").GetProperty("next_url").GetString() with
        | null -> None
        | url -> Some url

    let private baseUrl = "https://api.wanikani.com/v2/"

    let private makeRequest url =
        async {
            let! pages = depaginate getPage getNextUrl url
            let data =
                pages
                |> List.collect (fun p ->
                    p.GetProperty("data").EnumerateArray()
                    |> Seq.toList
                )
            return data
        }

    let getKanji () =
        async {
            let! data = makeRequest (baseUrl + "subjects?types=kanji")
            let kanji =
                data
                |> List.map (fun k ->
                    let level = k.GetProperty("data").GetProperty("level").GetInt32()
                    let kanji = k.GetProperty("data").GetProperty("characters").GetString()
                    level, Convert.ToChar kanji
                )
            return kanji
        }

    let getKanjiByLevel (kanjiList: (int * char) list) (level: int) =
        kanjiList
        |> List.filter (fun (l, _) -> l <= level)
        |> List.map snd

let writeWords (words: Jmdict.Word list) (path: string) =
    async {
        let lines = List.map Jmdict.wordToString words
        do! File.WriteAllLinesAsync(path, lines) |> Async.AwaitTask
    }

let main =
    async {
        let! allKanji = WaniKani.getKanji ()
        let! dict = Jmdict.loadDict ()
        if Directory.Exists("words") then
            Directory.Delete("words", true)
        Directory.CreateDirectory("words/all/") |> ignore
        Directory.CreateDirectory("words/common/") |> ignore
        for level in 1..60 do
            let allWords, commonWords =
                WaniKani.getKanjiByLevel allKanji level
                |> set
                |> Jmdict.getLearnableWords dict
            do! writeWords allWords (sprintf "words/all/level-%i" level)
            do! writeWords commonWords (sprintf "words/common/level-%i" level)
    }

Async.RunSynchronously main
