# 电解铝氧化铝浓度在线检测与槽控优化系统

## 系统架构

```
┌─────────────────────────────────────────────────────────────────────┐
│                        Docker Compose 编排                          │
│                                                                     │
│  ┌──────────────┐   ┌──────────────┐   ┌──────────────────────┐    │
│  │  ZigBee      │   │  C# Backend  │   │  SQL Server 2022     │    │
│  │  Simulator   │──▶│  .NET 8 API │──▶│  AluminaDetectionDB  │    │
│  │  (Python)    │   │  :5000       │   │  :1433               │    │
│  └──────────────┘   │              │   │                      │    │
│                     │  ┌────────┐  │   │  Tables:             │    │
│                     │  │MediatR │  │   │  - PotInfo (200)     │    │
│                     │  │Pipeline│  │   │  - PotRealtimeData   │    │
│                     │  └───┬────┘  │   │  - VoltageFeature    │    │
│                     │      │       │   │  - ConcentrationHist │    │
│                     │  ┌───┴────┐  │   │  - FeedingRecord     │    │
│                     │  │4 Svc   │  │   │  - AlarmRecord       │    │
│                     │  │Singleton│  │   │  - ApplicationLogs   │    │
│                     │  └────────┘  │   └──────────────────────┘    │
│                     │              │                                 │
│                     │  ZigBeeReceiver ──▶ FeaturesExtractedEvent    │
│                     │  ConcentrationEstimator ──▶ FeedingCommand    │
│                     │  AnodeEffectPredictor ──▶ EffectQuenchCmd    │
│                     │  AlarmController ──▶ MQTT + SignalR          │
│                     └──────┬───────┘                                │
│                            │                                        │
│              ┌─────────────┼─────────────┐                          │
│              ▼             ▼             ▼                          │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐             │
│  │  MQTT Broker │  │  SignalR Hub │  │  Application │             │
│  │  Mosquitto   │  │  WebSocket   │  │  Insights    │             │
│  │  :1883/:9001 │  │  /hubs/*     │  │  + Serilog   │             │
│  └──────────────┘  └──────┬───────┘  └──────────────┘             │
│                           │                                        │
│                           ▼                                        │
│                    ┌──────────────┐                                 │
│                    │  Frontend    │                                 │
│                    │  Canvas 2D   │                                 │
│                    │  potline_view│                                 │
│                    │  pot_detail  │                                 │
│                    └──────────────┘                                 │
└─────────────────────────────────────────────────────────────────────┘
```

### 数据流

```
ZigBee模拟器(200台/15s) → POST /api/potdata/zigbee
  → ZigBeeReceiver.ReceiveAsync()
    → MediatR.Send(ZigBeeDataReceivedCommand)
      → 特征提取(VoltageFeatureExtractor + FFT)
      → SVR浓度估计(ConcentrationEstimator)
        → Publish(ConcentrationEstimatedEvent)
          → AlarmController: 一级浓度告警检查
          → ConcentrationEstimator: 补料判断 → FeedingRequiredCommand
      → RF阳极效应预测(AnodeEffectPredictorService)
        → Publish(AnodeEffectPredictedEvent)
          → AlarmController: 二级效应告警
          → EffectQuenchCommand: 效应熄灭程序执行
    → SignalR推送 → 前端Canvas更新
    → MQTT推送(优先级队列 + 令牌桶限流, QoS 1)
```

### 技术栈

| 层 | 技术 | 说明 |
|---|---|---|
| 后端 | C# .NET 8 Web API | MediatR CQRS, Singleton + IServiceScopeFactory |
| 数据库 | SQL Server 2022 | EF Core, 定期索引维护+备份 |
| 消息 | Mosquitto MQTT 2.0 | QoS 1, ACL控制, 优先级+令牌桶限流 |
| 实时 | SignalR WebSocket | 15秒全量推送+告警即时推送 |
| 前端 | Canvas 2D + SignalR JS | potline_view.js + pot_detail.js |
| 监控 | Serilog + Application Insights | Console/File/MSSqlServer三写 |
| 压缩 | Brotli + Gzip Response Compression | Optimal级别 |
| 模拟 | Python 3.12 + requests | 200台/15秒, 动态注入场景 |

## 快速部署

### 前置条件

- Docker 20.10+
- Docker Compose V2+

### 一键启动

```bash
# 克隆项目
cd AI_solo_coder_task_A_070

# 构建并启动所有服务
docker-compose up -d --build

# 查看服务状态
docker-compose ps

# 查看后端日志
docker-compose logs -f backend
```

### 服务端口

| 服务 | 端口 | 用途 |
|---|---|---|
| C# Backend | 5000 | API + SignalR + Swagger |
| SQL Server | 1433 | 数据库 |
| MQTT Broker | 1883 | MQTT TCP |
| MQTT WebSocket | 9001 | MQTT over WebSocket |

### 环境变量

| 变量 | 默认值 | 说明 |
|---|---|---|
| `APPINSIGHTS_CONNECTION_STRING` | 空 | Azure Application Insights连接串 |
| `ConnectionStrings__DefaultConnection` | docker-compose内配置 | SQL Server连接串 |
| `Mqtt__Broker` | mqtt-broker | MQTT Broker主机名 |
| `Mqtt__Port` | 1883 | MQTT Broker端口 |

## 单独运行(开发模式)

### 启动后端

```bash
cd backend/AluminaDetection.Api

# 安装依赖
dotnet restore

# 运行(需本地SQL Server)
dotnet run
```

### 启动模拟器

```bash
cd simulator

# 安装依赖
pip install requests

# 默认200台/15秒/mixed场景
python zigbee_simulator.py --url http://localhost:5000

# 指定参数
python zigbee_simulator.py --url http://localhost:5000 --pots 50 --interval 10 --scenario anode_effect
```

## ZigBee模拟器用法

### 命令行参数

```
python zigbee_simulator.py [OPTIONS]

选项:
  --url URL              后端API地址 (默认: http://localhost:5000)
  --interval SECONDS     上报间隔秒数 (默认: 15)
  --pots COUNT           模拟电解槽数量 (默认: 200)
  --scenario SCENARIO    注入场景 (默认: mixed)
  --workers COUNT        线程池大小 (默认: 20)
  --inject-interval N    注入间隔覆盖(ticks, 0=自动)
```

### 场景说明

| 场景 | 说明 | 适用测试 |
|---|---|---|
| `normal` | 纯正常消耗，无注入 | 基线功能验证 |
| `low_concentration` | 定期注入浓度下降事件 | SVR估计+补料控制验证 |
| `anode_effect` | 定期注入阳极效应前兆 | RF预测+效应熄灭验证 |
| `mixed` | 混合注入(默认) | 全链路集成测试 |

### 注入机制

**浓度下降事件** (`ConcentrationDropEvent`):
- 加速氧化铝消耗率(0.2~0.5/tick)
- 可能抑制自动补料(40%概率)
- 持续20~80个tick(5~20分钟)

**阳极效应前兆** (`AnodeEffectPrecursor`) 四阶段:

| 阶段 | 持续ticks | 电压行为 | 噪声倍率 |
|---|---|---|---|
| 1-缓升期 | >80 | 基线缓慢上升(+0.005~0.02/tick) | 1.5~3.0x |
| 2-噪声期 | 40~80 | 上升+噪声增大 | 逐渐增至5x |
| 3-跳变期 | 15~40 | 上升加速+随机跳变 | 增至8x |
| 4-临界期 | <15 | 急剧上升+频繁跳变 | 增至15x |

### Docker中运行模拟器

```bash
# 使用docker-compose(随服务自动启动)
docker-compose up -d simulator

# 手动运行单次模拟
docker-compose run --rm simulator \
  python zigbee_simulator.py \
  --url http://backend:5000 \
  --pots 100 \
  --interval 10 \
  --scenario low_concentration
```

## 数据库维护

### 自动维护

Docker部署时，SQL Server Agent自动执行：

| 作业 | 时间 | 内容 |
|---|---|---|
| `Alumina_DailyBackup` | 每日 03:00 | 全量压缩备份 + 清理7天前备份 |
| `Alumina_IndexMaintenance` | 每周日 02:00 | 重建碎片率>30%的索引 + 全表统计信息更新 |

### 手动维护

```sql
-- 查看索引碎片
SELECT OBJECT_NAME(ind.object_id), ind.name, stat.avg_fragmentation_in_percent
FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, 'LIMITED') stat
JOIN sys.indexes ind ON stat.object_id = ind.object_id AND stat.index_id = ind.index_id
WHERE stat.avg_fragmentation_in_percent > 10;

-- 手动重建索引
EXEC sp_RebuildFragmentedIndexes @FragmentationThreshold = 30.0;

-- 手动更新统计信息
EXEC sp_UpdateStatistics;

-- 手动备份
EXEC sp_BackupDatabase @BackupPath = N'/var/opt/mssql/backup/';
```

## 监控与日志

### Serilog输出目标

| Sink | 说明 | 保留策略 |
|---|---|---|
| Console | 实时终端输出 | 无 |
| File | `/app/logs/alumina-{Date}.log` | 滚动30天 |
| MSSqlServer | `ApplicationLogs`表 | 按需清理 |

### Application Insights

设置环境变量启用：
```bash
export APPINSIGHTS_CONNECTION_STRING="InstrumentationKey=xxx;IngestionEndpoint=https://xxx.applicationinsights.azure.com/"
```

功能：
- HTTP请求追踪(含慢请求>2s告警)
- 依赖追踪(SQL/MQTT/HTTP)
- 性能计数器
- 实时指标流(Live Metrics)

### MQTT QoS

| Topic | QoS | 说明 |
|---|---|---|
| `aluminum/alarm/{potId}` | AtLeastOnce (QoS 1) | 告警消息确保送达 |
| `aluminum/status/{potId}` | AtMostOnce (QoS 0) | 状态消息允许丢失 |

## 前端

浏览器访问 `http://localhost:5000`，系统自动加载：

- **车间布局** (potline_view.js): 10行×20列 Canvas绘制，4色浓度状态，点击查看详情
- **槽详情弹窗** (pot_detail.js): 8小时电压/电流趋势图，最近10次下料记录
- **告警面板**: 实时报警列表，一级(浓度)橙色/二级(效应)红色

前端资源通过 Brotli/Gzip 压缩传输，压缩级别 Optimal。
