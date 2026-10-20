using System.Security.Cryptography;
using AsterismApp;

internal static class NotebookChecks
{
    internal static void Run(string root)
    {
        var directory = Path.Combine(root, "notebooks");
        Directory.CreateDirectory(directory);
        var service = new NotebookDocumentService(directory);
        var path = Path.Combine(directory, "학습.IPYNB");
        const string json = """
        {"nbformat":4,"nbformat_minor":5,"metadata":{"language_info":{"name":"python"},"custom":"keep me"},"cells":[
          {"cell_type":"markdown","source":["# 설명 $x^2$\n","[[연결 자료]]\n","![plot](attachment:plot.png)\n","<script>alert('bad')</script>"],"attachments":{"plot.png":{"image/png":"aGVsbG8="}}},
          {"cell_type":"code","execution_count":7,"source":["# 주석\n","print('안녕')"],"outputs":[
            {"output_type":"stream","name":"stdout","text":["안녕\n","결과 검색어"]},
            {"output_type":"display_data","data":{"image/png":"aGVs\nbG8=","text/plain":"그림 설명"}},
            {"output_type":"execute_result","data":{"text/html":["<table><tr><td>표</td></tr></table>","<script>alert('bad')</script>"],"text/plain":"표 검색어"}},
            {"output_type":"error","ename":"ValueError","evalue":"실패","traceback":["\u001b[31mtrace\u001b[0m"]},
            {"output_type":"display_data","data":{"application/vnd.jupyter.widget-view+json":{"model_id":"x"}}}
          ]},
          {"cell_type":"raw","source":"원시 텍스트 <tag>"}
        ]}
        """;
        File.WriteAllText(path, json);
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        var note = service.Load().Single();
        if (!note.IsReadOnly || !note.IsNotebook || note.IsPlainText || !note.Body.Contains("결과 검색어") || !note.Body.Contains("표 검색어") || note.Body.Contains("aGVsbG8="))
            throw new Exception("노트북 읽기 전용 인식 또는 검색용 텍스트 추출 실패");
        var document = NotebookDocumentService.ReadDocument(path);
        if (document.Cells.Count != 3 || document.Cells[1].Source != "# 주석\nprint('안녕')" || document.Cells[1].ExecutionCount != "7")
            throw new Exception("노트북 문자열·문자열 배열 셀 파싱 실패");
        var html = MarkdownPreviewRenderer.RenderNotebook(path, directory);
        if (!html.Contains("language-python") || !html.Contains("tok-comment") || !html.Contains("In [7]") || !html.Contains("data:image/png;base64,aGVsbG8="))
            throw new Exception("노트북 코드 문법 강조·실행 번호·이미지 출력 실패");
        if (!html.Contains("sandbox=\"\"") || !html.Contains("default-src &#39;none&#39;") || html.Contains("<script>alert('bad')</script>") || !html.Contains("ValueError: 실패") || html.Contains('\u001b'))
            throw new Exception("노트북 HTML 격리 또는 오류 출력 표시 실패");
        if (!html.Contains("node-note://note/") || !new NoteLinkService().ExtractTargets(note.Body).Contains("연결 자료"))
            throw new Exception("노트북 위키 링크 참여 실패");
        var tree = new VaultTreeService(Path.Combine(root, "notebook-order"));
        var item = tree.Build(directory, [note], [directory], new HashSet<string>(), "결과 검색어").Single();
        if (!item.IsNotebook || item.NotebookIconOpacity != 1 || item.NoteDotOpacity != 0 || item.IsVirtual)
            throw new Exception("노트북 탐색기 표시·본문 검색 실패");
        var copy = service.Duplicate(path);
        if (!SHA256.HashData(File.ReadAllBytes(copy.Path)).SequenceEqual(hash)) throw new Exception("노트북 복제 시 원본 변경");
        var renamed = service.Rename(path, "새 이름.ipynb");
        var folder = Path.Combine(directory, "하위");
        Directory.CreateDirectory(folder);
        var moved = service.Move(renamed.Path, folder);
        if (!SHA256.HashData(File.ReadAllBytes(moved.Path)).SequenceEqual(hash)) throw new Exception("노트북 열람·이름 변경·이동 시 원본 변경");
        var invalid = Path.Combine(directory, "손상.ipynb");
        File.WriteAllText(invalid, "{bad json");
        if (NotebookDocumentService.ReadDocument(invalid).Error is null || !service.Load().Any(item => item.Path == invalid))
            throw new Exception("손상된 노트북 안내 및 탐색기 유지 실패");
        var empty = Path.Combine(directory, "빈 노트북.ipynb");
        File.WriteAllText(empty, "{\"nbformat\":4,\"cells\":[]}");
        if (!MarkdownPreviewRenderer.RenderNotebook(empty, directory).Contains("빈 노트북입니다")) throw new Exception("빈 노트북 표시 실패");
        var hidden = Path.Combine(directory, ".ipynb_checkpoints");
        Directory.CreateDirectory(hidden);
        File.WriteAllText(Path.Combine(hidden, "checkpoint.ipynb"), json);
        if (service.Load().Any(item => item.Title == "checkpoint")) throw new Exception("체크포인트 중복 노출");
        try { service.Move(copy.Path, root); throw new Exception("저장소 외부 이동이 허용됨"); }
        catch (InvalidOperationException) { }
        Console.WriteLine("Notebook checks passed.");
    }
}
