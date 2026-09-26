import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from trading.review import main, parse_request, validate_response
from trading.conversation import CHAT_SCHEMA

ACCOUNT='0123456789abcdef01234567'

class TextProtocolTests(unittest.TestCase):
    def test_korean_emoji_and_combining_characters_survive_utf8(self):
        question='한글 질문 🚀📉 🧑‍💻 e\u0301\r\n두 번째 줄'
        wire=json.dumps({'action':'chat','question':question},ensure_ascii=False).encode('utf-8')
        self.assertEqual(parse_request(wire)['question'],question)
        self.assertEqual(parse_request(b'\xef\xbb\xbf'+wire)['question'],question)

    def test_utf16_json_escapes_form_valid_emoji(self):
        value=parse_request(b'{"question":"\\ud83d\\ude80"}')
        self.assertEqual(value['question'],'🚀')
        self.assertEqual(value['question'].encode('utf-8'),b'\xf0\x9f\x9a\x80')

    def test_cp949_input_is_rejected_instead_of_silently_corrupting_question(self):
        wire=json.dumps({'question':'한글 질문'},ensure_ascii=False).encode('cp949')
        with self.assertRaisesRegex(ValueError,'Trading.exe'):
            parse_request(wire)

    def test_unpaired_surrogates_are_reported_before_model_request(self):
        for raw in (b'{"question":"\\ud800"}',b'{"question":"\\udcff"}'):
            with self.assertRaisesRegex(ValueError,'손상된 문자'):
                parse_request(raw)
        with self.assertRaisesRegex(ValueError,'손상된 문자'):
            validate_response({'answer':'bad'+chr(0xd800)},CHAT_SCHEMA)

    def test_main_emits_protocol_error_for_wrong_encoding_without_creating_history(self):
        with tempfile.TemporaryDirectory() as temporary:
            raw=json.dumps({'action':'chat','account':ACCOUNT,'question':'한글 질문'},
                           ensure_ascii=False).encode('cp949')+b'\n'
            output=io.StringIO()
            with io.TextIOWrapper(io.BytesIO(raw),encoding='utf-8',errors='surrogateescape') as stream, \
                 patch('sys.stdin',stream),patch('sys.stdout',output), \
                 patch('trading.review.HERE',Path(temporary)), \
                 patch('trading.conversation.codex_response',side_effect=AssertionError('no CLI')):
                with self.assertRaises(SystemExit): main()
            value=json.loads(output.getvalue())
            self.assertEqual(value['type'],'error')
            self.assertIn('Trading.exe',value['message'])
            self.assertFalse((Path(temporary)/'private_state').exists())

    def test_main_chat_persists_and_returns_original_unicode(self):
        question='한국어 질문 🚀🧑‍💻\n다음 줄'
        reply='한글 답변 📈'
        raw=json.dumps({'action':'chat','account':ACCOUNT,'days':30,'question':question},
                       ensure_ascii=False).encode('utf-8')+b'\n'
        with tempfile.TemporaryDirectory() as temporary:
            output=io.StringIO()
            with io.TextIOWrapper(io.BytesIO(raw),encoding='utf-8',errors='surrogateescape') as stream, \
                 patch('sys.stdin',stream),patch('sys.stdout',output), \
                 patch('trading.review.HERE',Path(temporary)), \
                 patch('trading.conversation.codex_response',return_value={'answer':reply}) as model:
                main()
            value=json.loads(output.getvalue().splitlines()[-1])
            self.assertEqual(value['type'],'result')
            self.assertEqual(value['question'],question)
            self.assertEqual(value['answer'],reply)
            saved=json.loads(Path(value['path']).read_text(encoding='utf-8'))
            self.assertEqual(saved['messages'][0]['content'],question)
            self.assertEqual(saved['messages'][1]['content'],reply)
            self.assertIn('한국어 질문 🚀',model.call_args.args[0])

if __name__=='__main__': unittest.main()
