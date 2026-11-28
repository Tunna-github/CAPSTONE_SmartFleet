# Hướng dẫn tiếp tục test SmartFleet tại nhà

Cập nhật ngày **28/11/2026**, nhánh `feat/aiven-warehouses-task-resume`.
Đây là cấu hình demo JetRacer vật lý hiện tại. Các phần kỹ thuật bổ sung nằm ở
[physical-mission-demo.md](physical-mission-demo.md),
[mqtt-hotspot.md](mqtt-hotspot.md), [Aiven](aiven-shared-maps.md).

## 1. Trạng thái bàn giao

| Thành phần | Cấu hình hiện tại |
| --- | --- |
| Repo trên laptop | `C:\CAPSTONE\CAPSTONE_SmartFleet` |
| Web | `http://localhost:8080` |
| Database | PostgreSQL Aiven dùng chung, cần Internet |
| Map đang chạy | **Warehouse 1 (Map 1)**, warehouse ID **2**, map 3×3 |
| Map còn lại | **Current Warehouse (Map 2)**, warehouse ID **1**, là warehouse độc lập |
| Robot demo | **R03**, loại **PHYSICAL** |
| R01 / R02 | Robot SIMULATION, giữ nguyên dữ liệu; không dùng chung topic với R03 |
| MQTT từ backend | `mqtt:1883` trong mạng Docker |
| MQTT từ JetRacer | `192.168.137.1:1883` trên hotspot laptop |
| JetRacer / Jupyter | `192.168.137.168` / `http://192.168.137.168:8888/lab` |
| Folder chạy trên robot | `/home/jetson/Public/SmartFleet_Physical_Demo/` |
| Tuyến demo | **QR6 → QR7**, Canned Food → Cleaning Supplies |
| Tốc độ vật lý mặc định | Lệnh tiến **0,10 m/s**, chưa phải tốc độ thực đã đo |
| LiDAR | **Bắt buộc trong cấu hình đang dùng**, không bật chế độ bỏ LiDAR |

Lần kiểm tra cuối: Docker backend/mqtt/web đang chạy; Webots không chạy. Controller
R03 đã về IDLE, MQTT connected, READY=true, camera đọc QR6, cảm biến hợp lệ và lệnh
động cơ bằng 0. **T026 đã được phục hồi về QUEUED**, lần chạy cũ vẫn được giữ trong
lịch sử. Đây là ảnh chụp trạng thái bàn giao, phải kiểm tra lại khi khởi động tại nhà.

Đã kiểm tra phần mềm, camera/cảm biến đứng yên và ACK navigation thật qua broker.
**Chưa xác nhận một lượt chạy vật lý hoàn tất QR6 → QR7.** Demo hai robot vật lý hoặc
một vật lý + một Webots qua giao lộ cũng còn cần test thực tế. Không dùng kết quả
unit test làm bằng chứng xe đã chạy đúng quãng đường hoặc bán kính cua.

## 2. Bật lại Docker trên chính laptop này

1. Kết nối Internet để truy cập Aiven, bật Docker Desktop ở chế độ Linux containers.
2. Bật Mobile hotspot, cho JetRacer kết nối vào hotspot.
3. Đợi laptop có địa chỉ hotspot `192.168.137.1` trước khi bật broker.
4. Mở PowerShell:

```powershell
Set-Location C:\CAPSTONE\CAPSTONE_SmartFleet
git status --short
git branch --show-current
git pull --ff-only origin feat/aiven-warehouses-task-resume
docker compose up -d --build backend mqtt web
docker compose ps
docker compose port mqtt 1883
```

Nếu có thay đổi local chưa commit, giữ lại chúng trước khi pull; không dùng reset
hoặc clean để ép mất thay đổi. Ba service backend/mqtt/web phải chạy và port MQTT
phải là `192.168.137.1:1883`. Mở `http://localhost:8080`.

File **`.env` riêng trên laptop** hiện chọn các Compose overlay sau:

```dotenv
COMPOSE_FILE='compose.yaml;compose.charging.yaml;compose.aiven.yaml;compose.mqtt-hotspot.yaml;compose.physical-line.yaml'
AIVEN_MAP_KEY='map1'
AIVEN_WAREHOUSE_ID='2'
AIVEN_CHARGING_ENABLED='false'
AIVEN_MANIFEST='config/scene-manifest.json'
MQTT_HOTSPOT_ADDRESS='192.168.137.1'
MQTT_HOTSPOT_PORT='1883'
```

Đây chỉ là các giá trị **không bí mật để đối chiếu**, không phải toàn bộ `.env`.
Giữ nguyên các giá trị kết nối Aiven, JWT, tài khoản và đường dẫn world/volume
đang có trong file riêng. Không thay toàn bộ `.env` bằng đoạn trên.
Overlay physical-line bật R03, tắt auto-assign khi tạo task và cho Webots vào
profile tùy chọn. Không cần bật Webots để test một JetRacer.

**Không chạy lại setup_demo, migration, import/restore database hoặc chuyển Map 2
chỉ để khởi động lại demo.** Không dùng `docker compose down -v`, `volume prune`
hay xóa volume: volume đang lưu cả trạng thái điều phối cần giữ.

Khi kết thúc: Pause nếu đang làm task, chờ xe đứng yên; hoàn tất hoặc Recovery
task có hàng trước khi tắt. Sau đó chạy cell `agent.close()` khi đã sẵn sàng kết
thúc controller, rồi `docker compose stop backend web mqtt`. Giữ nguyên volume.

## 3. MQTT/hotspot: có phải cấu hình lại mỗi lần không?

**Không, nếu vẫn laptop này, `.env` này và IP hotspot không đổi.** Port publish
đã lưu trong overlay. Robot chủ động kết nối TCP tới `192.168.137.1:1883`; cùng
kết nối đó nhận lệnh và gửi telemetry/ACK hai chiều. Không cần broker Windows
riêng hoặc cài broker lên Jetson. Địa chỉ internal `mqtt` chỉ dành cho backend.

Nếu đổi IP hotspot hoặc cổng, phải sửa cả endpoint Docker và `brokerHost` /
`brokerPort` của profile robot. IP Wi-Fi Internet của laptop không phải địa chỉ
hotspot. Kiểm tra IP thật bằng `ipconfig`; không đoán địa chỉ robot sau khi DHCP đổi.

Kiểm tra cấu hình hiện tại mà không đổi gì:

```powershell
python tools/mqtt_hotspot.py
```

Chỉ khi chưa có port publish, dùng `python tools/mqtt_hotspot.py --enable`.
Công cụ chỉ khởi động broker và sẽ từ chối recreate broker chưa publish nếu
backend/Webots đang chạy. Hoàn tất/recover task rồi dừng backend/Webots trước
nếu gặp thông báo này. Không tắt tiến trình chiếm cổng một cách tùy tiện.

Test hai chiều không chạy động cơ, dùng Python/Paramiko sẵn có trên laptop và
host key SSH đã xác minh:

```powershell
python tools/check_mqtt_hotspot.py --robot-host 192.168.137.168 --ssh-user jetson --known-hosts "$env:LOCALAPPDATA\SmartFleet\jetracer-connection\known_hosts"
```

Công cụ hỏi mật khẩu riêng, không lưu mật khẩu. Nếu thiếu Paramiko, có thể bỏ
qua công cụ này và xem MQTT connected trên notebook; không cần cài gì lên robot.
Nếu thực sự bị Windows Firewall chặn, xem hướng dẫn rule giới hạn IP robot ở
[mqtt-hotspot.md](mqtt-hotspot.md). Không tắt toàn bộ firewall.

## 4. ROS trên robot: chỉ chạy khi chưa có

Mở Jupyter tại IP robot. **Nếu các terminal ROS đang chạy thì giữ nguyên, không
chạy thêm bản thứ hai.** Lỗi `new node registered with same name` trước đây là do
hai node `/jetracer` tranh tên rồi respawn, không phải bằng chứng robot bị hỏng.

Nếu vừa bật nguồn robot và ROS chưa chạy, mở **ba terminal riêng**, mỗi terminal
source môi trường rồi chỉ chạy một lệnh launch tương ứng:

```bash
# Terminal 1: driver robot (để terminal này chạy)
source /opt/ros/melodic/setup.bash
source ~/catkin_ws/devel/setup.bash
roslaunch jetracer jetracer.launch
```

```bash
# Terminal 2: camera
source /opt/ros/melodic/setup.bash
source ~/catkin_ws/devel/setup.bash
roslaunch jetracer csi_camera.launch
```

```bash
# Terminal 3: LiDAR
source /opt/ros/melodic/setup.bash
source ~/catkin_ws/devel/setup.bash
roslaunch jetracer lidar.launch
```

Nếu muốn xem camera HTTP thì chạy `rosrun web_video_server web_video_server`
trong terminal riêng đã source ROS. Đây chỉ là preview, **không phải đường lấy ảnh
điều khiển**. Controller dùng raw ROS `/csi_cam_0/image_raw` qua shared memory.

Để chẩn đoán trong terminal khác đã source, chạy `rosnode list`, rồi lần lượt
`rostopic hz /csi_cam_0/image_raw`, `rostopic hz /scan`, `rostopic hz /odom_raw`.
Nhấn Ctrl+C để dừng từng **lệnh xem hz**, không phải terminal launch đang chạy.
LiDAR có process chưa đủ; cần dữ liệu `/scan` mới và hướng quét phía trước đúng.
Nếu driver LiDAR timeout cổng USB thì kiểm tra dây/nguồn/cổng thực, không đổi driver
hoặc bỏ guard để xe chạy. Không cần cài lại OpenCV/NumPy/ROS/paho.

## 5. Code và cell chạy notebook

Code nguồn mới nằm trong **`Robot code/physical/`** của repo. Trên robot, tất cả
module phải nằm cùng thư mục `~/Public/SmartFleet_Physical_Demo/`. Khi cập nhật,
upload các file `.py`, `.ipynb` và profile **example** của folder này bằng Jupyter;
giữ bản sao trước khi ghi đè. Không ghi đè `physical-profile.json` riêng nếu đã
chỉnh cho robot. `Robot code/main_code.py` là code tham khảo cũ, không phải runtime
hiện đang chạy trên JetRacer.

Các file runtime phải cùng phiên bản, đặc biệt `mission.py`, `runtime.py`,
`protocol.py`, `raw_camera.py`, `notebook_panel.py`. Copy một file tùy ý có thể để
lại lỗi cũ vì các module phụ thuộc nhau. Không copy dữ liệu journal từ robot khác.

Đóng controller cũ bằng `agent.close()` hoặc STOP controller, dừng notebook lái xe
cũ/Keyboard Lab để không tranh `cmd_vel`. **Nếu đang có task, việc đóng controller
sẽ tạo hold cần Recovery; dùng Pause trên web nếu muốn tiếp tục cùng task.**
Sau khi cập nhật file, dùng **Kernel → Restart Kernel**, không restart driver ROS.

Có thể tạo notebook Python 3 trong folder trên và dán **một cell** sau. Khi profile
đã tồn tại, cell giữ nguyên cấu hình riêng và kiểm tra đúng endpoint/robot trước
khi khởi động. `line-demo-profile.example.json` là mẫu cần dùng; mẫu
`physical-profile.example.json` dành cho chế độ metric khác.

```python
from pathlib import Path
import json
import sys

folder = Path.home() / "Public" / "SmartFleet_Physical_Demo"
sys.path.insert(0, str(folder))
profile_path = folder / "physical-profile.json"
if not profile_path.exists():
    profile = json.loads((folder / "line-demo-profile.example.json").read_text())
    profile.update({
        "robotCode": "R03",
        "mqttClientId": "smartfleet-physical-R03",
        "brokerHost": "192.168.137.1",
        "brokerPort": 1883,
        "supervisedNoLidar": False,
    })
    profile_path.write_text(json.dumps(profile, indent=2))

profile = json.loads(profile_path.read_text())
assert profile["robotCode"] == "R03", "Sai robot: kiểm tra physical-profile.json"
assert profile["mqttClientId"] == "smartfleet-physical-R03", "Sai MQTT client ID"
assert profile["navigationMode"] == "MARKER_LINE_DEMO", "Sai chế độ demo"
assert profile["brokerHost"] == "192.168.137.1" and profile["brokerPort"] == 1883
assert not profile.get("supervisedNoLidar", False), "Demo hiện tại yêu cầu LiDAR"

from runtime import PhysicalRuntime
from notebook_panel import show

if "agent" in globals():
    agent.close()
agent = PhysicalRuntime(profile_path).start()
panel = show(agent)
```

Sau khi cell trả về, controller vẫn chạy trong thread nền; kernel Idle là bình
thường. Đợi tối đa khoảng 20 giây cho ROS IO khởi tạo. **Không dùng Run All** trên
notebook mẫu: cell cuối `agent.close()` sẽ tắt controller vừa bật.

Điều kiện trước khi assign:

- Notebook: MQTT connected, State **IDLE**, Backend READY **True**.
- Xe đứng yên, camera nhận **Visible ArUco 6 / Last checkpoint 6**, line visible.
- Web: R03 **ONLINE / AVAILABLE**, đúng warehouse, không còn task/incident giữ xe;
  pin ít nhất 20% và đủ điều kiện dispatch.
- Đặt xe hướng từ QR6 tới QR7 theo băng đen; đường phía trước trống, cảm biến mới.

Camera chỉ thấy line không có nghĩa đã đọc được QR. Mã phải nằm trong ảnh, không
bị che/cắt dưới đáy ảnh; đặt giấy phẳng, chừa viền trắng không dính băng đen.
Không cần nhập chiều cao camera, kích thước mã, encoder scale hay heading để demo
marker/line này. Encoder/IMU vẫn được dùng cho telemetry và xác nhận đứng yên.

## 6. Test task QR6 → QR7 theo flow bình thường

1. Đăng nhập tài khoản Operator hiện có. Các tài khoản demo do `.env` cấu hình;
   dùng mật khẩu đã được cấp, không reset database để đổi mật khẩu.
2. Vào Transport tasks. Có thể dùng lại **T026 nếu vẫn QUEUED** và đúng tuyến;
   không cần tạo thêm task chỉ vì lần chạy trước thất bại.
3. Nếu tạo mới: chọn **Warehouse 1 (Map 1)** → pickup **Canned Food (QR6)** →
   delivery **Cleaning Supplies (QR7)**. Dùng MANUAL và assign **R03**; tải hàng
   không vượt cấu hình robot (R03 hiện giới hạn 1 kg).
4. Vào Live tracking & handover. Xe tại pickup phải dừng, báo chờ verification.
5. Kiểm tra hàng thật, đánh dấu xác nhận giao nhận; nhập **QR6** hoặc **WH1:QR6**.
   Nếu task yêu cầu package code thì nhập đúng package code riêng, không dùng QR6
   thay cho mã hàng. Việc đọc QR bằng camera không tự thay thao tác Operator.
6. Backend xác nhận pickup, gửi CONTINUE; controller lấy ACK navigation và GO mới,
   sau đó mới follow line. Chờ xe đi tới mã **QR7**, dừng và hiện delivery form.
7. Xác nhận giao hàng, nhập **QR7** hoặc **WH1:QR7**, xác nhận hàng và package nếu
   được yêu cầu. Chỉ sau backend COMPLETE và ACK, task mới COMPLETED.
8. **Để notebook tiếp tục chạy.** Controller trở về IDLE chờ task kế tiếp; không
   tự kết thúc chương trình. Demo thẳng không tự quay đầu hoặc trở về sạc.

Nếu giao task quay ngược, phải xử lý xong task hiện tại, đặt/hướng lại xe và tạo
session mới. Không ép tuyến ngược hoặc tuyến có QR trung gian chưa in. Map 2 có
lane node khác; không dùng nó cho đường QR6 → QR7 đang dán trên sàn.

Tốc độ x4 của Webots không áp dụng cho xe thật. Không tăng tốc trong lúc sửa ACK
hay mất line; lệnh 0,10 m/s cũng chưa chứng minh tốc độ bánh xe thực tế đúng 0,10.

## 7. Pause, Resume, STOP và Recovery

| Thao tác | Ý nghĩa |
| --- | --- |
| Pause trên web | Dừng chuyển động, giữ cùng task, trạng thái hàng và khóa tài nguyên |
| Resume trên web | Tiếp tục cùng task nếu session/context còn và kiểm tra an toàn đạt |
| STOP controller / `agent.close()` | Kết thúc controller; task chưa xong được giữ để Recovery |
| Cancel | Hủy theo workflow, không tương đương Pause; demo vật lý chưa có tự về sạc |
| Restart kernel / mất MQTT giữa task | Có thể tạo FAILED/RECOVERY_HOLD, không tự chạy lại |

Nếu thất bại: giữ xe đứng yên, kiểm tra vị trí hàng rồi vào tài khoản Maintenance →
Robot safety/Pause → đúng task/R03. Nhập lý do, xác nhận physical recovery checklist
chỉ khi thực sự đã kiểm tra xe và hàng; dùng **Recover & Requeue (wait 30s)** khi
hàng đã ở pickup hoặc demo không có hàng. Nếu hàng đã nhận, phải trả hàng về đúng
pickup trước khi xác nhận returned. Không chọn bừa checkbox để mở khóa.

Task về QUEUED vẫn giữ failed assignment/mission và lịch sử verification. Robot
có thể còn MAINTENANCE: kiểm tra incident thật, nhập resolution note và Resolve;
sau khi telemetry mới, pin hợp lệ, không task đang active, đặt lại AVAILABLE.
Maintenance không có nghĩa mọi incident đều có thể Resolve ngay khi chưa đủ điều kiện.

**Giới hạn hiện tại:** journal robot không tự xóa sau server Recovery. Nếu notebook
còn RECOVERY_HOLD/FAILED, đừng tạo task mới hoặc xóa `mission-hold.json`. Cần đối
chiếu đúng taskId/assignmentId/missionId: task QUEUED, assignment/mission cũ FAILED,
lệnh dừng đã ACK, không assignment active khác và hàng đã được xác nhận an toàn;
sau đó mới đóng controller thuộc lần chạy đó và lưu trữ journal cũ thành bản sao
recovered trước khi mở session mới. Phần đồng bộ reset journal này hiện chưa có
nút tự phục vụ hoàn chỉnh; gửi log để xử lý có kiểm chứng nếu gặp lại. Restart
kernel đơn thuần không bỏ hold. Không sửa DB hoặc ép READY để vượt qua nó.

## 8. Các lỗi đã gặp và cách kiểm tra

| Hiện tượng | Cần kiểm tra |
| --- | --- |
| QR6 bị mismatch | Cập nhật backend và adapter cùng phiên bản; QR6/QR06 được chấp nhận khi đúng checkpoint của mission. Full code WH1:QR6 cũng hợp lệ; WH2:QR6 sai warehouse vẫn bị từ chối. Mã hàng là trường riêng. |
| Đã xác nhận nhưng xe không đi | Xem state/error ở notebook và web. VERIFICATION_STATUS chờ ở trạm là bình thường; SCAN phải nhận `navigation-ack`, rồi GO mới. FAILED/RECOVERY_HOLD phải Recovery, không tăng tốc hay lặp confirm. |
| Application ACK timeout | Ghi lại pending kind/event ID/task/assignment/mission. Giữ stopped; kiểm tra Internet Aiven, backend log và broker. Không giả ACK. |
| READY False | Xem QR có còn trong ảnh, stopped feedback, dữ liệu camera/odom/IMU/LiDAR, session/map, mission hold và lỗi đang hiển thị. |
| Native import failed -4 / Illegal instruction | Code mới đặt OPENBLAS_CORETYPE=ARMV8 và OPENBLAS_NUM_THREADS=1 trước khi import NumPy trên aarch64. Restart kernel sau cập nhật raw_camera.py. Giữ bộ OpenCV 4.6/NumPy có sẵn; không pip upgrade tùy tiện. |
| ROS IO did not initialize | Xem ros-io.log trong run hiện tại; timeout mới 20s. Kiểm tra ROS master và các topic, controller tranh cmd_vel. Không chạy lại jetracer.launch khi node đang tồn tại. |
| LiDAR timeout / stale | Kiểm tra nguồn, USB và `/scan`; không đổi sang fake CLEAR hoặc no-LiDAR để mở task. |
| Web login 502 / request timeout | Xem `docker compose ps`, backend/web logs, Internet tới Aiven. Backend phải thực sự khởi động; không phải mặc định sai mật khẩu. |
| Shared database lease bị chiếm | Chỉ một backend điều phối cùng Aiven. Dừng phiên cũ đúng cách, chờ lease hết hạn; không giết session DB bất kỳ hoặc tắt cơ chế khóa. |
| Web không hiện robot khi lỗi mạng | Fetch lỗi không chứng minh database mất robot. Kiểm tra backend trước, không đăng ký trùng thiết bị. |

Lệnh xem log trên laptop (log có thể chứa dữ liệu nội bộ, không đăng công khai):

```powershell
docker compose logs --tail 120 backend
docker compose logs --tail 80 web mqtt
```

Logs robot nằm dưới `~/.smartfleet/physical/R03/`: `mission-hold.json` nếu có và
các thư mục `run-*` chứa `events.jsonl`, `ros-io.log`, `bridge.log`, `aruco.jsonl`
tùy thành phần. Giữ journal và run tương ứng khi báo lỗi. Gửi ảnh state/error,
task/assignment/mission ID và log gần lúc lỗi; che JWT, password, connection string.

## 9. Hai robot và giao lộ — test sau lượt thẳng

Hai JetRacer cần **hai identity PHYSICAL khác nhau**, mqttClientId khác nhau và
cùng backend/coordinator/warehouse/map. R01/R02 hiện SIMULATION nên không dùng tên
đó cho notebook vật lý. Robot vật lý thứ hai cần đăng ký và được allowlist; hiện
chưa có cấu hình/kết nối đã kiểm chứng cho nó.

Một JetRacer + Webots cũng cần đúng world và map revision, khác robotCode, giữ
Dispatch:AllowMixedRobotTypes=true. Webots đang tắt trong overlay physical; không
tự bật robot ảo chạy các task cũ khi chưa kiểm tra danh sách task và sensor pose.
Robot ảo không phải vật cản thật để LiDAR nhìn thấy.

Cả hai dùng APPROACH → GO → ENTER → EXIT/ACK và khóa giao lộ hiện có. Nhận mã giữa
giao lộ không đồng nghĩa xe đã ra hết; cần mã exit nằm ngoài vùng giao và camera
không còn thấy mã trung tâm. Controller thẳng chưa thực hiện cua 90° hay U-turn.
Kích thước 2,4 m, bán kính cua và vận tốc đo bằng encoder là công việc đo riêng;
không chốt kích thước map từ ảnh hoặc kết quả giả lập.

## 10. Git, cấu hình riêng và bản sao lưu

Git lưu source và hướng dẫn; **không chứa dữ liệu database, secret `.env`, CA
`.aiven/ca.pem`, profile/journal robot hoặc các dump**. Trên chính laptop này,
giữ nguyên chúng khi pull. Aiven chứa task/map/station dùng chung; Git pull không
restore database hay đồng bộ file notebook đang chạy trên robot.

Nếu chuyển sang laptop khác, phải chuyển cấu hình riêng qua kênh riêng, có CA và
quyền truy cập Aiven, rồi làm theo [Aiven](aiven-shared-maps.md) để join môi
trường hiện có. Không thực hiện lại initial migration/import. **Không chạy hai
backend điều phối cùng lúc.** Với người chỉ xem web, dùng địa chỉ laptop đang
chạy backend, không khởi động một backend mới.

Bản backup riêng trên máy này nằm ở
`%LOCALAPPDATA%\SmartFleet\aiven-migration\`, gồm dump trước các lần Recovery
T024/T025/T026. Giữ folder đó ngoài Git; có dump không có nghĩa cần restore.
Không restore chồng dữ liệu Aiven đang dùng để thử lại task. Map 1/Map 2 và dữ liệu
task hiện tại đã tách warehouse; giữ nguyên các bản đã có.

## 11. Kiểm thử bàn giao

Kết quả chạy lại trước commit được ghi trong
[physical-handoff-validation-2026-11-28.json](physical-handoff-validation-2026-11-28.json).
Hai file validation cũ là **snapshot phần mềm trước triển khai**, không mô tả trạng
thái database/Docker mới nhất. Test UI dùng API mock; không gửi task tới động cơ.

Tại nhà ưu tiên một lượt QR6 → QR7 theo mục 6, rồi kiểm tra task COMPLETED và
notebook trở lại IDLE. Nếu chỉ muốn test nhanh việc giao task/xe bắt đầu chạy,
dùng **Pause trên web** để dừng, sau đó Resume cùng task khi đủ điều kiện; không
ấn STOP rồi kỳ vọng Resume giữ nguyên session.
