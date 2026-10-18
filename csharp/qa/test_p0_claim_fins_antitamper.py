"""Synthetic fixture tests for Agent05 verifier; never mislabel as real five-process."""
from __future__ import annotations

from contextlib import closing
import hashlib
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest
import uuid

from p0_claim_fins_antitamper import (EvidenceError, ATTACKS, mutate_case,
                                      snapshot_original, tamper_all, verify_case)


def uid():
    return str(uuid.uuid4())


def raw_packet(write, address, sid, words=(1,), response_words=None):
    words = tuple(words)
    req = bytes([0x80,0,2,0,1,0,0,99,0,sid,1,2 if write else 1,
                 0x82,address>>8,address&255,0,0,len(words)])
    if write:
        req += b''.join(w.to_bytes(2,'big') for w in words)
    rsp = bytearray(req[:12]);rsp[0]=0xc0
    rsp[3:6]=req[6:9];rsp[6:9]=req[3:6]
    rsp += bytes([0,0])
    if not write:
        rsp += b''.join(w.to_bytes(2,'big') for w in (response_words or words))
    return req.hex().upper(),bytes(rsp).hex().upper()


def hh(h):
    return hashlib.sha256(bytes.fromhex(h)).hexdigest()


def fixture(root: Path, mode='normal'):
    # Note: REAL acceptance ALWAYS uses A0 real OS processes; these rows only
    # test the attack-detector itself, never the AOI Host.
    (root/'master').mkdir(parents=True)
    identities={t:dict(workpieceId=uid(),traceId=uid(),releaseId='SIM_TEST_REL',epoch=1,
                       barcode=f'ITEM{t}') for t in (1,2,3)}
    with closing(sqlite3.connect(root/'master/sim-full-cycle.db')) as db,db:
        db.execute('CREATE TABLE sim_full_tokens(token INTEGER,identity_json TEXT)')
        db.executemany('INSERT INTO sim_full_tokens VALUES(?,?)',
                       [(t,json.dumps(v)) for t,v in identities.items()])
    a1rows=[];a7intents=[];a7events=[];packets=[]
    seq=0; a1seq=0; a7seq=0
    def append_packet(token,write,addr,words=(1,),response=None, outcome='OK'):
        nonlocal seq
        seq+=1
        request,answer=raw_packet(write,addr,seq,words,response)
        if outcome=='INJECTED_TERMINAL_ACK_LOST_AFTER_APPLY':answer=''
        packet=dict(Sequence=seq,ReceivedUtc='2026-10-18T01:00:00+00:00',
                    MonotonicTimestamp=seq*100,Sid=seq,Token=token,
                    IsWrite=write,Address=addr,Values=list(words) if write else [],
                    RequestHex=request,ResponseHex=answer,Outcome=outcome)
        packets.append(packet)
        return packet
    def a1(item,intent,kind,step=-1,claim=None,proof=None,readback=None,identity=None,token=None):
        nonlocal a1seq
        a1seq+=1
        entry=dict(sequence=a1seq,kind=kind,workpieceId=item,intentId=intent,
                   stepIndex=step,claimId=claim,wireProof=proof,readbackValue=readback)
        if identity is not None:
            entry['intent']=dict(token=token,workpiece=identity)
        a1rows.append((a1seq,intent,item,json.dumps(entry)))
    def a7(attempt,phase,step,op=None,sid=None,addr=None,val=None,request=None,reply=None,read=None):
        nonlocal a7seq
        a7seq+=1
        a7events.append((a7seq,attempt,phase,step,op,sid,addr,val,
                         hh(request) if request else None,
                         hh(reply) if reply else None,read))

    for token in (1,2,3):
        identity=identities[token];item=identity['workpieceId'];intent=uid();attempt=uid()
        fault=mode!='normal' and token==3
        expected='BarcodeMismatch' if fault else 'Normal'
        a7intents.append((attempt,item,identity['traceId'],1,identity['releaseId'],token,expected,'Reject'))
        a1(item,intent,'intentPrepared',identity=identity,token=token)
        scanner=append_packet(token,False,2099,words=(1,),response=(1,))
        a7(attempt,'ScannerRequestVerified',0,uid(),scanner['Sid'],2099,1,
           scanner['RequestHex'],scanner['ResponseHex'])
        steps=[(2099,4),(1016,2)] if fault else [(1016,2),(2099,2)]
        if mode=='ack-lost' and token==3: steps=steps[:1]
        for n,(addr,val) in enumerate(steps):
            claim=uid()
            a1(item,intent,'writeClaimed',n,claim)
            outcome='INJECTED_TERMINAL_ACK_LOST_AFTER_APPLY' if mode=='ack-lost' and token==3 else 'OK'
            packet=append_packet(token,True,addr,words=(val,),outcome=outcome)
            proof=dict(operationId=claim,sid=packet['Sid'],dmAddress=addr,
                       value=val,replyVerified=False)
            a1(item,intent,'wireSendObserved',n,claim,proof)
            a7(attempt,'StepPrepared',n+1,claim,packet['Sid'],addr,val,packet['RequestHex'])
            a7(attempt,'WireSent',n+1,claim)
            if outcome != 'OK':
                a7(attempt,'AckUnknown',n+1,claim)
                a1(item,intent,'outcomeUnknown',n,claim)
                continue
            proof2=dict(proof,replyVerified=True)
            a1(item,intent,'finsAckObserved',n,claim,proof2)
            a7(attempt,'AckAccepted',n+1,claim,packet['Sid'],addr,val,
               packet['RequestHex'],packet['ResponseHex'])
            read=append_packet(token,False,addr,words=(1,),response=(val,))
            a7(attempt,'ReadbackVerified',n+1,uid(),read['Sid'],addr,None,
               read['RequestHex'],read['ResponseHex'],val)
            a1(item,intent,'readbackConfirmed',n,claim,readback=val)
    with closing(sqlite3.connect(root/'master/final-plc-output.db')) as db,db:
        db.execute('CREATE TABLE a1_final_output_events(sequence INTEGER,intent_id TEXT,workpiece_id TEXT,event_json TEXT)')
        db.executemany('INSERT INTO a1_final_output_events VALUES (?,?,?,?)',a1rows)
    with closing(sqlite3.connect(root/'master/timing-events.sqlite')) as db,db:
        db.execute('CREATE TABLE final_output_intents(attempt_id TEXT,workpiece_id TEXT,trace_id TEXT,epoch INTEGER,release_id TEXT,token INTEGER,scenario TEXT,disposition TEXT)')
        db.execute('CREATE TABLE final_output_audit_events(sequence INTEGER,attempt_id TEXT,phase TEXT,step_number INTEGER,operation_id TEXT,fins_sid INTEGER,dm_address INTEGER,value INTEGER,request_sha256 TEXT,response_sha256 TEXT,readback_value INTEGER)')
        db.executemany('INSERT INTO final_output_intents VALUES (?,?,?,?,?,?,?,?)',a7intents)
        db.executemany('INSERT INTO final_output_audit_events VALUES (?,?,?,?,?,?,?,?,?,?,?)',a7events)
    (root/'p0-fins-wire.jsonl').write_text(''.join(json.dumps(p)+'\n' for p in packets),encoding='utf8')


class IndependentClaimWireTamperTests(unittest.TestCase):
    def setUp(self):
        self.t=tempfile.TemporaryDirectory(prefix='a5-sixth-fixtures-')
        self.root=Path(self.t.name)
        self.good=self.root/'good';fixture(self.good)

    def tearDown(self):self.t.cleanup()

    def test_positive_untouched_synthetic_fixture(self):
        result=verify_case(self.good,'normal')
        self.assertEqual((result['writes'],result['readbacks'],result['claims']),(6,6,6))

    def test_positive_mismatch_and_acklost_synthetic_fixtures(self):
        for scenario in ('mismatch','ack-lost'):
            with self.subTest(scenario=scenario):
                path=self.root/scenario;fixture(path,scenario)
                result=verify_case(path,scenario)
                self.assertEqual(result['writes'],5 if scenario=='ack-lost' else 6)
                self.assertEqual(result['readbacks'],4 if scenario=='ack-lost' else 6)

    def test_all_single_field_attacks_against_independent_snapshot(self):
        tampered=self.root/'tampered'
        result=tamper_all(self.good,'normal',tampered)
        self.assertEqual(set(result['negativeAttacks']),set(ATTACKS))

    def test_negative_wrong_claim_id_not_equivalent_to_wire_operation(self):
        dest=self.root/'claim';snapshot_original(self.good,dest)
        mutate_case(dest,'claim-id')
        with self.assertRaises(EvidenceError) as caught:
            verify_case(dest,'normal')
        self.assertEqual(caught.exception.code,'CLAIM_ID_MISMATCH')

    def test_negative_byte_tamper_refuses_even_unchanged_register_metadata(self):
        dest=self.root/'request';snapshot_original(self.good,dest)
        mutate_case(dest,'request-hex')
        with self.assertRaises((EvidenceError,Exception)):
            verify_case(dest,'normal')

    def test_real_unknown_ACK_classification_and_claim_identity_are_tamper_resistant(self):
        lost = self.root / 'lost-for-unknown'
        fixture(lost, 'ack-lost')
        result = tamper_all(lost, 'ack-lost', self.root / 'isolated-unknown-attacks')
        self.assertEqual(set(result['negativeAttacks']),
                         set(ATTACKS) | {'unknown-ack-promoted', 'unknown-claim-id'})
        self.assertEqual(verify_case(lost, 'ack-lost')['readbacks'], 4)

    def test_original_immutable_after_tamper(self):
        source=(self.good/'p0-fins-wire.jsonl').read_bytes()
        results=tamper_all(self.good,'normal',self.root/'all')
        self.assertEqual(len(results['negativeAttacks']),len(ATTACKS))
        self.assertEqual((self.good/'p0-fins-wire.jsonl').read_bytes(),source)
        self.assertEqual(verify_case(self.good,'normal')['status'],'PASS')


if __name__=='__main__': unittest.main()
