import struct
import unittest
from faulty_vplc import barcode_words, respond

class VplcProtocolTests(unittest.TestCase):
    def setUp(self):
        self.words=[0]*5000
        self.words[2000:2010]=barcode_words('SIM-0001')
    @staticmethod
    def request(write,address,values,count=None):
        count=len(values) if count is None else count
        return bytes([128,0,2,0,1,0,0,99,0,17,1,2 if write else 1,130])+struct.pack('>HBH',address,0,count)+(struct.pack('>'+('H'*count),*values) if write else b'')
    def test_barcode_words_are_big_endian_and_padded(self):
        self.assertEqual(struct.pack('>10H',*barcode_words('SIM-0001')),b'SIM-0001'+b'\0'*12)
    def test_read_yields_fins_success_with_matching_sid(self):
        packet=self.request(False,2000,[],10)
        reply,op=respond(packet,self.words)
        self.assertEqual(op,('read',2000,10))
        self.assertEqual(reply[:1],b'\xc0')
        self.assertEqual(reply[9],17)
        self.assertEqual(reply[12:14],b'\0\0')
        self.assertEqual(reply[14:],b'SIM-0001'+b'\0'*12)
    def test_virtual_output_isolated_from_other_dm_words(self):
        packet=self.request(True,1016,[2])
        reply,op=respond(packet,self.words)
        self.assertEqual(op,('write',1016,1))
        self.assertEqual(reply[12:14],b'\0\0')
        self.assertEqual(self.words[1016],2)
        self.assertEqual(self.words[2099],0)
        self.assertEqual(self.words[2000:2010],barcode_words('SIM-0001'))
    def test_reject_out_of_bounds_and_bad_memory_area(self):
        for bad in (self.request(False,4999,[],2),self.request(False,1016,[],0),
                    self.request(False,2000,[],65),
                    self.request(False,2000,[],1)[:12]+b'\x81'+self.request(False,2000,[],1)[13:]):
            reply,operation=respond(bad,self.words)
            self.assertEqual(reply,b'')
            self.assertIsNone(operation)
    def test_truncated_write_must_not_mutate_virtual_memory(self):
        before=self.words.copy()
        packet=self.request(True,1016,[1])[:-1]
        reply,_=respond(packet,self.words)
        self.assertEqual(reply,b'')
        self.assertEqual(self.words,before)

if __name__=='__main__':
    unittest.main()
