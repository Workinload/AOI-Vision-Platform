#!/usr/bin/env python3
"""Populate plan-only reproducible scenario/gate/risk tables. No AOI application tests are run."""
from pathlib import Path
import csv
P=Path(__file__).resolve().parents[1]

def write(name,head,rows):
    with (P/name).open('w',encoding='utf-8-sig',newline='') as f:
        w=csv.writer(f);w.writerow(head)
        for i,r in enumerate(rows,1):
            if len(r)!=len(head):raise ValueError((name,i,len(r),len(head)))
            w.writerow(r)
    print(name,len(rows))

# Fields: ScenarioId, Group, Owner, Trigger, ExpectedMachineAssertion, MandatoryEvidence, RelatedTasks, Phase, ExecutionStatus
S=[
('SC-001','HAPPY','A10','相邻两工件独立11通道正常处理','主机恰好两次FinalResult;从机结果已落库;每件11图对应一ID','master/slave SQLite查询 + PeerLink messages + vPLC transactions + 11 result assets','W049','S04'),
('SC-002','IDENTITY','A1','两件使用完全相同raw_barcode','两件WorkpieceId不同且槽位/图像不串','Workpiece ledger快照+事件链','W035,W037','S03'),
('SC-003','IDENTITY','A1','11卡实时图片来自不同拍照时刻与工件','实时卡显示各自WorkpieceId;同件图库只有目标工件','11卡Qt截图+gallery DB查询','W069,W071','S05'),
('SC-004','CAPTURE','A3','SDK硬触发前帧后PLC请求','AcquisitionCoordinator唯一关联,无软件软触发误动作','frame/capture gate trace','W036,W046','S03'),
('SC-005','CAPTURE','A3','先PLC请求后有效帧','合法deadline内一对一关联;过期标MISSING','frame/PLC时间轴和预期断言','W036,W046','S03'),
('SC-006','CAPTURE','A3','面阵无帧/SDK超时','站位MISSING/ERROR,不产生整料OK','capture timeout日志+FinalResult','W032,W039,W107','S08'),
('SC-007','CAPTURE','A3','SDK FrameId重复或内存buffer复用','新帧不覆盖仍在推理/保存的图像;重复隔离','decoded image sha256与quarantine','W030,W037,W107','S08'),
('SC-008','CAPTURE','A3','线扫8200x15000拆分正常','shared capture group一帧,CH08/09或CH10/11双路分行7500/7499','原图尺寸hash+拆分坐标和DB父资产','W051,W060,W066','S04'),
('SC-009','CAPTURE','A3','线扫丢行/帧损坏','两条派生通道检测状态受影响;无错误CamDone;报警','frame flags+alarm+PLC txn','W032,W091,W107','S08'),
('SC-010','CAPTURE','A3','SDK掉线后迟帧到达并重连','旧Attempt/Epoch进入quarantine且不计结果','epoch/capture时序+单件查询','W032,W037,W107','S08'),
('SC-011','IDENTITY','A1','重拍AttemptNo递增','保留原Attempt审计但Final只按有效Attempt','同一工件multi-attempt DB查询','W037,W040,W100','S08'),
('SC-012','IDENTITY','A1','上一Epoch帧晚于下一Epoch到达','不得补新Epoch的缺图;标隔离','event payload+FinalResult断言','W037,W043,W107','S08'),
('SC-013','IDENTITY','A1','漏一个关键检测通道','最终状态HOLD/UNDECIDED,不写模拟OK','expected vs received channels+PLC trace','W040,W049','S04'),
('SC-014','PLC','A2','FINS WORD/2WORD float32 BADC测试矢量','报文长度/SID/endcode正确且解码与样例一致','raw pcap bytes与golden fixture','W023,W024','S02'),
('SC-015','PLC','A2','D2000旧读20WORD但PLC提供10WORD','原始raw_words保存,长度冲突警告,不猜后段零','vPLC raw words+decoded+报警','W024,W025,W108','S08'),
('SC-016','PLC','A2','D1013双用途或D1000/D1030地址重叠','Registry红标且真实生产写入拒绝','SignalRegistry列值+deny audit','W025,W026,W108','S08'),
('SC-017','PLC','A2','CamDone=2但MockInference报NG','采图应答和最终D1016独立;不算整件OK','PLC序列+FinalResult与StationResult','W039,W046,W047,W055','S04'),
('SC-018','PLC','A2','末端正常扫码完成','模拟 D1016(OK/NG)后D2099=2顺序符合被批准分支','seqno/txn id/readback trace','W047,W108','S08'),
('SC-019','PLC','A2','末端扫码失败或错位','D2099=3/4先于D1016=2,错误不可当NG缺陷数字','sim PLC transaction order + alarm','W041,W047,W108','S08'),
('SC-020','PLC','A2','对非127.0.0.1端点尝试模拟写','连接/写被阻断且生成审计事件;不得有外向实际PLC UDP包','OS网络证据/拦截日志','W021,W026,W108','S08'),
('SC-021','PEER','A5','断从机PeerLink60s后重连','本地Outbox重发,主机最终仅一次计数','outbox/inbox/ack+event trace','W043,W044,W106','S08'),
('SC-022','PEER','A5','相同MessageId相同hash重复发两次','接收端InboxDedup只应用一次但可再次ACK','inbox count + dedup log','W043,W106','S08'),
('SC-023','PEER','A5','相同MessageId不同payload_hash','拒绝并报警,不静默覆盖原内容','tamper rejection+alarm+inbox bytes','W043,W044,W106','S08'),
('SC-024','PEER','A5','ACK发出前或后Kill Slave Core','PERSISTED与APPLIED区分,重启无业务效果丢失或加倍','DB快照+ack状态+进程退出码','W043,W062,W106','S08'),
('SC-025','ASSET','A6','AssetStore fsync后但DB提交前kill','启动可reconcile PENDING/孤儿,保留证据不伪AVAILABLE','file hash+DB状态前后','W059,W064,W112','S08'),
('SC-026','ASSET','A6','无损压缩原图后重新解码','像素数值/位深/stride规范化规则的hash匹配','raw pixel SHA256 before/after','W059,W060','S04'),
('SC-027','ASSET','A6','磁盘满或图片损坏','不丢图却报告保存成功,生成HOLD/Alarm','asset states+root alarm+error','W065,W091,W112','S08'),
('SC-028','ASSET','A6','SQLite WAL运行中创建备份并异机恢复','完整数据与图片/Release/model hash能恢复且追溯查询无孤儿','backup manifest+sqlite integrity+hash','W064,W102,W112','S08'),
('SC-029','ROI','A4','DetectionROI外检测框但DisplayROI裁掉','整料NG仍计且UI报画面外缺陷并支持全图定位','raw/result overlay+Qt screenshot','W052,W076,W110','S08'),
('SC-030','VISION','A4','模型不存在、shape不符、超时','站Result ERROR,最终不可OK,保存故障现场','model registry+result+alarm','W053,W058,W099','S07'),
('SC-031','OFFLINE','A4','同一张源图用Draft与Active离线A/B','结果归OFFLINE schema,不上报vPLC不计产量','ReplayLab DB/traffic + StatsReadModel','W096,W098,W104','S07'),
('SC-032','SOLUTION','A7','产品A单站保存草稿后切B','A Draft变, B配置不变且Active未更改','draft revisions+solution diff','W083,W084,W094','S06'),
('SC-033','SOLUTION','A7','多人同 base_revision 修改同一站','一方乐观锁失败,不会后写覆盖','error code+DB revisions+audit','W084,W090','S06'),
('SC-034','SOLUTION','A7','换型From A To B 双机Prep+Readback+Commit','双方同Release/Epoch/设备目标值,打开Ready','deployment record+both readbacks','W086,W087,W088,W095','S06'),
('SC-035','SOLUTION','A7','从机Prepared后断网或PLC readback不符','进入FAILED_SAFE/HOLD,不开放新料,不得仅DB回滚','deployment failure+vPLC/state trace','W087,W095,W113','S08'),
('SC-036','SECURITY','A9','无权限用户直接发送IPC配置/写值命令','Core后端拒绝;有审计/报警;UI权限不可绕过','IPC result + auth audit','W089,W090,W111','S08'),
('SC-037','SECURITY','A9','报警已ACK而设备仍故障','Alarm remains ACTIVE直到恢复条件满足','AlarmEvent lifecycle+Qt UI screenshot','W091,W092,W110','S08'),
('SC-038','LOG','A9','跨Master/Slave UTC时钟模拟偏移','保留时钟质量/因果序列号,不会用UTC比较顺序错配','peer heartbeat/time quality trace','W019,W042,W103','S08'),
('SC-039','GUI','A8','用户关闭重开Qt UI','Core持续处理真实11通道,UI重连快照无假LIVE','5进程pid+UI真截图+result events','W081,W082,W110','S08'),
('SC-040','GUI','A8','点击D1013信号表和设备地图热点','ST09设备/CH08/09/PLC连线互相定位且标风险','真实Qt两方向点击录屏/截图','W072,W073,W074,W110','S08'),
('SC-041','STATS','A6','一块NG含多缺陷/重拍且重新统计','NG件数=1,缺陷数按实例,日周产量不翻倍','SQL oracle+Stats snapshots','W100,W101,W114','S08'),
('SC-042','RELEASE','A10','从发布ZIP解压运行Windows PE 5进程','实际EXE+Qt插件+模型前端加载,证据区分Wine与原生','PE audit+wine exit code+screenshots','W116,W117,W118,W120','S09'),
('SC-043','UI','A8','在2K、1080p与高DPI操作11路视图','11图及12格同屏不需滚主视区,坐标和框准确','Qt screenshot+scenegraph/image reload counters','W068,W070,W080,W110','S08'),
('SC-044','SECURITY','A9','模拟D99/D100心跳丢失或复位请求','不将心跳/Ready等于全机可生产;故障可见,未核现场点标未知','vPLC trace+health/alarm+UI','W025,W091,W108','S08'),
('SC-045','SOLUTION','A7','双机换型中断电并在仅一机Commit后重启','禁止双机分裂脑Ready；启动恢复持久DeploymentRecord，直到双方Release/Epoch/PLC读回一致保持HOLD','两机重启进程树+DeploymentRecord+PLC readback+deny Ready日志','W087,W095,W113','S08'),
('SC-046','SECURITY','A9','S01阶段未经授权用户直发未知IPC修改命令','未完成W089 RBAC前一律拒绝且有审计；上线RBAC后也拒绝越权变更','UIReadAPI负面IPC回执+审计+原始DB对比','W016,W089,W111','S08'),
('SC-047','PLC_UI','A8','PLC LegacySourceValue与ActualLiveValue、Draft不一致','图形和表格同源，旧静态候选不得显示为现场已确认，也不自动开放写','SignalRegistry+QT界面双向截图+字段来源校验','W025,W073,W074','S05'),
('SC-048','RELEASE','A10','从Windows发布包删除一个必需QtQuick/QML import plugin','发布闭包审计必须失败，恢复插件后两Qt UI可从干净目录运行','QML import resolution报告+真实Windows PE运行/退出码','W117,W118','S09'),
('SC-049','ROI','A4','对同件只改变DisplayROI裁切范围','前后FinalQuality和全部DetectionROI结果完全一致，画面外缺陷仍可定位','变更前后DB比较+overlay坐标+Qt截图','W052,W058,W076','S08'),
('SC-050','DETERMINISM','A10','同seed下改变进程PID/UTC时间和日志目录重演场景','规范化业务事件digest一致且因果序确定；原始审计时间仍保存','两次scenario replay+规范化事件摘要/hash+因果屏障记录','W034,W049','S04'),
('SC-051','OFFLINE','A4','离线Lab主动尝试向vPLC/生产IPC发写入命令','网络/权限边界拒绝，生产DB/Outbox/PLC字节计数无变化','OS socket记录+生产DB及vPLC事务前后比较','W096,W108,W111','S08'),
('SC-052','FIELD_BOUNDARY','A10','模拟114用例大部分通过但FG仍未核','SIM_READY候选不得将14个FG自动标已核，也不开放Field写策略','字段状态表+发版Manifest+写网关deny日志','W006,W026,W108,W122','S09'),
]
write('SCENARIO_MATRIX.csv',['ScenarioId','Group','Owner','FaultOrTrigger','ExpectedMachineAssertion','MandatoryEvidence','RelatedTaskIds','DuePhase','ExecutionStatus'],[list(x)+['NOT_RUN'] for x in S])

gates=[
('G00','S00','W001-W010','基线/114/21/14/合同冻结与SIM写隔离','A0','A10','baseline_lock.json;contracts.v1;rq_mapped','NOT_RUN'),
('G01','S01','W011-W022','Linux Qt+2 Core+2 UI实际启动;双库与IPC','A0','A10','processlist;ctest;db_paths','NOT_RUN'),
('G02','S02','W023-W034','虚拟PLC FINS报文+9物理设备模拟+simctl五进程','A2','A10','pcap;camera readbacks;health json','NOT_RUN'),
('G03','S03','W035-W047','完整身份/Station/Final State Machine与PeerLink合同','A1','A10','idempotency;epoch;missing result traces','NOT_RUN'),
('G04','S04','W048-W067','W049以W058/W060/W061/W062/W063等真实产物为先决；11路带Overlay结果与独库/Outbox双机E2E','A6','A10','raw pixel hash;master/slave DB;vPLC txn','NOT_RUN'),
('G05','S05','W068-W082','真Qt两分辨率11同屏+地图表联动；报警/统计/工位保存只作为分阶段UI骨架，后端G06/G07再签','A8','A10','real Qt screenshot;gui automation evidence','NOT_RUN'),
('G06','S06','W083-W095','双机设备+vPLC读回后方可PREPARED_BOTH，Qt工位参数保存真实接入RBAC/Draft，报警Lifecycle全接线','A7','A10','deployment failure matrix;auth audit','NOT_RUN'),
('G07','S07','W096-W104','离线Lab+追溯+W100完整周日报表及Qt图表复验+114 test runner','A4','A10','lab/production isolation;query/analytics oracle','NOT_RUN'),
('G08','S08','W105-W114','混沌压力安全负测;114需求SIM执行状态有证据','A10','A0','114 indexed TC+run logs+failure list','NOT_RUN'),
('G09','S09','W115-W122','Linux/Windows PE+QtQuick/QML插件闭包+完整ZIP，保存于Wine前；成品代码两轮复核','A0','A10','sourceZIP;WindowsZIP;hash;Wine actual EXE log','NOT_RUN'),
('FIELD','HARDWARE','W123-W130','14现场FG+真实SDK/PLC/机器人/工艺签核','现场有权负责人','A0+A10','真实字段只读证据与试生产签署','FIELD_PENDING')
]
write('EXIT_GATES.csv',['Gate','Phase','TaskRange','ExitConditions','Approver','IndependentVerifier','EvidenceNeeded','Status'],gates)
risks=[
('RISK-01','Critical','D1030/D1000定位地址冲突','现场PLC变量读回、坐标单位与单站签核','A2','FG-01','SIM只允vPLC,Field拒写'),
('RISK-02','Critical','D1013别名与线扫清料冲突','静态冲突注册表+PLC版本核查','A2','FG-02','需现场核未解一律禁写'),
('RISK-03','High','扫码20/40字节长度不一致','保留原始WORD/比较多组真件','A2','FG-03,FG-04','模拟覆盖两种长度,不猜'),
('RISK-04','Critical','D1016=2真实NG流向未知','只做vPLC末端行为;现场确认机械分流','A1','FG-05','不可将正常响应承诺成NG箱'),
('RISK-05','Critical','半机换型成功导致错误Ready','Stage/Prepare/Readback/Commit安全状态机','A7','FG-06,FG-10','任何读回不符则HOLD'),
('RISK-06','High','海康/Itek硬触发与配置不可信','各SDK MOCK;真驱动/型号现场验证','A3','FG-07,FG-11,FG-13','Mock的PASS!=实机PASS'),
('RISK-07','High','双光线扫分路方向误解','像素单测主从方向、现场光路标定','A3','FG-09','展示静态旧方向待验证'),
('RISK-08','High','无真实电池模型与真值','MockInference+可选通用YOLO smoke分开','A4','FG-14','禁止精度造假'),
('RISK-09','High','产线节拍与存储容量未知','123MB真尺寸压测与现场容量评估','A6','FG-08,FG-12','不设虚假硬阈值'),
('RISK-10','Critical','跨机工件混淆与重计','ID+Epoch+Attempt+Release,持久Outbox/Inbox','A1,A5','SIM','错误无法判则HOLD'),
('RISK-11','High','开发Agent公共DTO冲突','A0合同冻结、ADR和Owner路径','A0','SIM','不同Owner不得同写公共头'),
('RISK-12','High','Windows发布漏OpenVINO前端','依赖闭包+真ONNX加载/Wine运行','A10','SIM','先保存ZIP再Wine测试'),
('RISK-13','High','DB索引与原图跨资源不一致','PENDING/fsync/rename+恢复索引校验','A6','SIM','备份包含图像与模型'),
('RISK-14','High','Qt UI模拟显示成硬编码假数据或S05页面骨架假称全功能','从UIReadAPI查询真实Core,断链可见','A8','SIM','真实Qt截图及事件检查'),
('RISK-15','Critical','SIM进程意外访问真实PLC','编译时SIM_ONLY+loopback限定+网络负测','A2,A9','SIM','deny outbound;无法测时NOT_VERIFIED'),
('RISK-16','High','S05页面先于真正报警/产品草稿/完整周统计后端','UI集成两阶段证据矩阵；S06/S07二次复验','A8','SIM','未接入功能须显示未就绪;不允许冒称完成'),
('RISK-17','Critical','W049未等结果入库、Outbox事务及线扫父资产即宣布双机闭环','WBS显式DAG依赖+三方资产/DB/网络痕迹','A10','SIM','真实主从消息和图像事务前置完成'),
('RISK-18','High','Windows Qt Widgets样板不能证明Qt Quick/QML插件完备','W007最小QML编译+W117干净发布双Qt启动','A10','SIM','不能只看QT6Widgets或PE导入闭包'),
]
write('RISK_REGISTER.csv',['RiskId','Severity','Description','Mitigation','Owner','FieldGate','SafetyInvariant'],risks)
