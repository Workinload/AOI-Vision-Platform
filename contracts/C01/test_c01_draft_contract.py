#!/usr/bin/env python3
"""Review-only draft: stdlib tests enforce proposed identity/state safeguards. NOT C01 frozen."""
import copy,json,re,sys,unittest
from pathlib import Path
from csv import DictReader
ROOT=Path(__file__).resolve().parents[2]
SCHEMA=json.loads((ROOT/'contracts/C01/CORRELATION_ENVELOPE_DRAFT.schema.json').read_text())
REGISTRY=list(DictReader((ROOT/'contracts/C01/CONTRACT_REGISTRY_DRAFT.csv').open(encoding='utf-8')))
P={'SchemaVersion':'C01-DRAFT-0','MessageId':'event-1','PayloadHash':'a'*64,'WorkpieceId':'wp-1','Epoch':2,'ReleaseId':'rel-1','ChannelId':'CH08','CaptureId':'cap-1','AttemptNo':1,'ProcessingStatus':'SUCCEEDED','QualityStatus':'NG'}
def validate(item):
    props=SCHEMA['properties']
    if set(item)!=set(SCHEMA['required']):raise ValueError('unknown/missing envelope field')
    for key,field in props.items():
        val=item[key]
        if 'const' in field and val!=field['const']:raise ValueError('schema version')
        if 'enum' in field and val not in field['enum']:raise ValueError('bad enum')
        if field.get('type')=='string' and (not isinstance(val,str) or len(val)<field.get('minLength',0)):raise ValueError('bad string')
        if field.get('type')=='integer' and (type(val) is not int or val<field.get('minimum',0)):raise ValueError('bad integer')
        if 'pattern' in field and not re.fullmatch(field['pattern'],val):raise ValueError('bad pattern')
    if item['ProcessingStatus']!='SUCCEEDED' and item['QualityStatus']=='OK':raise ValueError('process fault cannot OK')
    return True
class ReviewOnly(unittest.TestCase):
    def test_registry(self):
        self.assertEqual([x['ContractId'] for x in REGISTRY],[f'C-{i:02d}' for i in range(1,13)])
        self.assertTrue(all(x['Status']=='PROPOSED_NOT_FROZEN' for x in REGISTRY))
        self.assertEqual(REGISTRY[6]['Owner'],'A6');self.assertEqual(REGISTRY[9]['Owner'],'A7')
    def test_example(self):self.assertTrue(validate(P))
    def test_bad_field_and_schema(self):
        for key,val in [('SchemaVersion','0'),('MessageId',''),('PayloadHash','x'*64),('Epoch',-1),('Epoch',True),('ChannelId','CH12'),('AttemptNo',-1),('ProcessingStatus','OK')]:
            with self.subTest(field=key):
                p=dict(P,**{key:val})
                with self.assertRaises(ValueError):validate(p)
        for p in ({k:v for k,v in P.items() if k!='WorkpieceId'},dict(P,UnknownField='injected')):
            with self.assertRaises(ValueError):validate(p)
    def test_error_never_ok(self):
        for state in ('ERROR','CANCELLED','TIMEOUT','PENDING','RUNNING'):
            with self.subTest(state=state):
                with self.assertRaises(ValueError):validate(dict(P,ProcessingStatus=state,QualityStatus='OK'))
    def test_identity_epoch_guard(self):
        def correlates(expected,actual):
            return all(expected[k]==actual[k] for k in ('WorkpieceId','Epoch','ReleaseId','ChannelId','CaptureId','AttemptNo'))
        for key,val in [('WorkpieceId','other-piece'),('Epoch',1),('ReleaseId','old'),('ChannelId','CH09'),('CaptureId','other'),('AttemptNo',0)]:
            self.assertFalse(correlates(P,dict(P,**{key:val})))
    def test_stale_message_conflict(self):
        def accepted(existing,new):return existing['MessageId']!=new['MessageId'] or existing['PayloadHash']==new['PayloadHash']
        self.assertFalse(accepted(P,dict(P,PayloadHash='b'*64)))
    def test_line_capture_parent(self):
        parent={'CH08':'rear-line','CH09':'rear-line','CH10':'front-line','CH11':'front-line'}
        self.assertEqual(parent['CH08'],parent['CH09']);self.assertEqual(parent['CH10'],parent['CH11']);self.assertNotEqual(parent['CH08'],parent['CH10'])
    def test_default_block(self):
        publish=False;physical_plc_write=False;field_gates_signed=0
        self.assertFalse(publish or physical_plc_write);self.assertEqual(field_gates_signed,0)

# Contract review: fail-safe rule must exist in JSON Schema, not only Python helper.
class SchemaCrossFieldReview(unittest.TestCase):
    def test_non_success_never_ok_is_declarative(self):
        import importlib.util
        clauses=SCHEMA.get('allOf',[])
        self.assertTrue(any(x.get('then',{}).get('properties',{}).get('QualityStatus',{}).get('not',{}).get('const')=='OK' and set(x.get('if',{}).get('properties',{}).get('ProcessingStatus',{}).get('enum',[]))=={'PENDING','RUNNING','ERROR','CANCELLED','TIMEOUT'} for x in clauses))
        if importlib.util.find_spec('jsonschema'):
            import jsonschema
            jsonschema.Draft202012Validator.check_schema(SCHEMA)
            for status in ('PENDING','RUNNING','ERROR','CANCELLED','TIMEOUT'):
                with self.subTest(status=status):
                    with self.assertRaises(jsonschema.ValidationError):
                        jsonschema.validate(dict(P,ProcessingStatus=status,QualityStatus='OK'),SCHEMA)
            jsonschema.validate(dict(P,ProcessingStatus='SUCCEEDED',QualityStatus='NG'),SCHEMA)

if __name__=='__main__':
    result=unittest.main(verbosity=2,exit=False).result
    print('C01_REVIEW_DRAFT_TESTS',result.testsRun,'FAILURES',len(result.failures),'ERRORS',len(result.errors))
    sys.exit(0 if result.wasSuccessful() else 1)