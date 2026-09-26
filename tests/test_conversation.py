import json
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch
from trading.config import load
from trading.history import HistoryStore, encode
from trading.conversation import chat, recent_messages, CHAT_SCHEMA

ACCOUNT='0123456789abcdef01234567'

class ConversationTests(unittest.TestCase):
    def setUp(self):
        temporary=tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root=Path(temporary.name)
        self.store=HistoryStore(self.root,ACCOUNT)
        self.config=load()

    def ask(self, question='현재 전략을 설명해줘', session=None, days=30):
        return chat(self.store,self.config,days,question,session)

    def test_followup_survives_process_reload_and_uses_fresh_data(self):
        prompts=[]
        def reply(prompt,schema,cancel):
            prompts.append(json.loads(prompt.split('CONTEXT:\n',1)[1]))
            return {'answer':'첫 답변' if len(prompts)==1 else '후속 답변'}
        with patch('trading.conversation.codex_response',side_effect=reply):
            first=self.ask()
            self.store.set_meta(sync_end=1234)
            second=self.ask('이전 답변의 검증 방법은?',first['conversation_id'])
        self.assertEqual(second['conversation_id'],first['conversation_id'])
        self.assertEqual(prompts[1]['previous_messages'],[
            {'role':'user','content':'현재 전략을 설명해줘'},
            {'role':'assistant','content':'첫 답변'}])
        self.assertEqual(prompts[1]['current_data']['coverage']['sync_end_ms'],1234)
        saved=json.loads(Path(second['path']).read_text(encoding='utf-8'))
        self.assertEqual(len(saved['messages']),4)
        self.assertEqual(saved['model'],'gpt-6-astra')
        self.assertEqual(saved['reasoning_effort'],'xhigh')
        self.assertNotIn(ACCOUNT,encode(prompts))
        self.assertNotIn('orders_enabled',encode(prompts))
        self.assertNotIn('initial_equity',encode(prompts))

    def test_missing_history_allows_strategy_question_without_fabricated_trades(self):
        with patch('trading.conversation.codex_response',return_value={'answer':'거래 표본 없음'}) as call:
            self.ask()
        context=json.loads(call.call_args.args[0].split('CONTEXT:\n',1)[1])
        self.assertEqual(context['current_data']['statistics']['verified_trades'],0)

    def test_account_and_period_cannot_reuse_foreign_conversation(self):
        with patch('trading.conversation.codex_response',return_value={'answer':'ok'}) as call:
            first=self.ask()
            other=HistoryStore(self.root,'fedcba9876543210fedcba98')
            with self.assertRaises(ValueError):
                chat(other,self.config,30,'질문',first['conversation_id'])
            with self.assertRaises(ValueError):
                self.ask('질문',first['conversation_id'],7)
        self.assertEqual(call.call_count,1)

    def test_failed_or_cancelled_reply_keeps_saved_dialogue(self):
        with patch('trading.conversation.codex_response',return_value={'answer':'ok'}):
            first=self.ask()
        path=Path(first['path']); before=path.read_bytes()
        for error in (RuntimeError('offline'),InterruptedError('cancel')):
            with patch('trading.conversation.codex_response',side_effect=error):
                with self.assertRaises(type(error)):
                    self.ask('다음 질문',first['conversation_id'])
            self.assertEqual(path.read_bytes(),before)
        self.assertEqual(list(path.parent.glob('*.tmp')),[])

    def test_empty_or_invalid_input_is_rejected_before_cli(self):
        with patch('trading.conversation.codex_response',side_effect=AssertionError('no CLI')):
            for question in ('','  ',None,'x'*4001):
                with self.assertRaises(ValueError): self.ask(question)
            for session in ('../secret','A'*32,'abc',7):
                with self.assertRaises(ValueError): self.ask(session=session)
            for days in (0,366):
                with self.assertRaises(ValueError): self.ask(days=days)

    def test_cancelled_job_does_not_create_transcript(self):
        event=threading.Event(); event.set()
        with self.assertRaises(InterruptedError):
            chat(self.store,self.config,30,'질문',cancel=event)
        self.assertFalse((self.root/'records').exists())

    def test_context_bounds_preserve_full_local_history(self):
        messages=[{'role':role,'content':str(i)} for i in range(15) for role in ('user','assistant')]
        result=recent_messages(messages)
        self.assertEqual(len(result),20)
        self.assertEqual(result[0],{'role':'user','content':'5'})
        self.assertEqual(len(messages),30)
        large=[{'role':role,'content':'x'*20000} for i in range(4) for role in ('user','assistant')]
        result=recent_messages(large)
        self.assertEqual(result[0]['role'],'user')
        self.assertLessEqual(sum(len(m['content']) for m in result),60000)

    def test_only_selected_account_and_period_report_is_included(self):
        directory=self.root/'records'/ACCOUNT/'analysis'; directory.mkdir(parents=True)
        (directory/'20260101-30days.md').write_text('older',encoding='utf-8')
        (directory/'20260201-30days.md').write_text('selected report',encoding='utf-8')
        (directory/'20260301-7days.md').write_text('wrong period',encoding='utf-8')
        with patch('trading.conversation.codex_response',return_value={'answer':'ok'}) as call:
            self.ask()
        context=json.loads(call.call_args.args[0].split('CONTEXT:\n',1)[1])
        self.assertEqual(context['latest_saved_report'],'selected report')

    def test_empty_response_does_not_save_successful_exchange(self):
        with patch('trading.conversation.codex_response',return_value={'answer':' '}):
            with self.assertRaises(ValueError): self.ask()
        self.assertEqual(list((self.root/'records'/ACCOUNT/'chat').glob('*.json')),[])

    def test_shared_cli_runner_pins_model_and_effort_without_execution_tools(self):
        from trading.review import codex_response
        calls=[]
        def run(args,cwd,cancel,timeout,input_text):
            calls.append(args)
            Path(args[args.index('--output-last-message')+1]).write_text('{"answer":"확인"}',encoding='utf-8')
            return 0,'','model: gpt-6-astra\\nreasoning effort: xhigh'
        with patch('trading.review.codex_status',return_value={'chatgpt_login':True,'path':'codex.exe'}), \
             patch('trading.review.run_process',side_effect=run):
            response=codex_response('test',CHAT_SCHEMA)
        self.assertEqual(response['answer'],'확인')
        args=calls[0]
        self.assertEqual(args[args.index('--model')+1],'gpt-6-astra')
        self.assertIn('model_reasoning_effort="xhigh"',args)
        self.assertEqual(args[args.index('--sandbox')+1],'read-only')
        for tool in ('shell_tool','unified_exec','code_mode','code_mode_host','apps'):
            self.assertEqual(args[args.index(tool)-1],'--disable')


    def test_analysis_report_records_same_pinned_model_and_effort(self):
        from trading.review import analyze
        answer={'summary':'검증 답변','observations':[],'symbol_feedback':[],'recommendations':[],'limitations':[]}
        with patch('trading.review.codex_response',return_value=answer):
            result=analyze(self.store,self.config,30,force=True)
        self.assertIn('Extra High (xhigh)',result['report'])
        files=list((self.root/'records'/ACCOUNT/'analysis').glob('*-evidence.json'))
        self.assertEqual(len(files),1)
        evidence=json.loads(files[0].read_text(encoding='utf-8'))
        self.assertEqual(evidence['codex'],{'model':'gpt-6-astra','reasoning_effort':'xhigh'})

if __name__=='__main__': unittest.main()
