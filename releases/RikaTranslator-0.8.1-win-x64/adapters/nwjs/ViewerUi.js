/* Local Chinese UI labels for Flower Knight Girl Viewer.
 * Only presentation text is changed. IDs, search values and scene data stay intact.
 */
(function () {
    "use strict";
    if (window.RikaViewerUiFactory) return;
    var labels = {
        "Controls": "操作说明", "Options": "设置", "Search": "搜索", "Return": "返回",
        "Loading Data Scripts": "正在加载数据脚本", "Character": "角色", "Artist": "画师", "CV": "配音演员",
        "Female": "女性标签", "Male": "男性标签", "Match All": "全部匹配", "Misc": "其他", "Location": "地点",
        "Exclusive Characters Only": "仅独占角色", "Main Series Characters Only": "仅正篇角色",
        "Favourites Only": "仅收藏", "Translated Only": "仅已有译文", "Cancel": "取消", "Reset": "重置",
        "No results": "没有匹配结果", "Scene Select": "场景选择", "Left Click": "鼠标左键", "Right Click": "鼠标右键",
        ": Start Scene,": "：开始场景，", ": Favourite Scene": "：收藏场景", "WASD/Arrow Keys": "WASD / 方向键",
        ": Move Cursor,": "：移动光标，", "Space/Enter": "空格 / 回车", ": Prev Page,": "：上一页，",
        ": Next Page,": "：下一页，", ": Toggle Favourite": "：切换收藏", ": Search,": "：搜索，",
        ": Controls,": "：操作说明，", ": Options,": "：设置，", ": Random,": "：随机，", ": Shuffle,": "：打乱顺序，",
        ": Switch H/Story,": "：切换场景 / 剧情，", ": Switch Scene/CG": "：切换场景 / CG",
        "Long Tap": "长按", ": Favourite,": "：收藏，", "Swipe Left": "向左滑动", "Swipe Right": "向右滑动",
        ": Prev Page": "：上一页", ": TL Mode": "：译文编辑模式", "Scene Viewer": "场景播放",
        ": Progress Scene,": "：推进对白，", "Scroll Up": "向上滚动", "Open Backlog": "打开对白历史",
        ": Toggle Auto Mode,": "：切换自动播放，", "Left Ctrl (Hold)": "按住左 Ctrl", ": Skip,": "：快进，",
        ": Close Scene,": "：关闭场景，", ": Hide Text Box": "：隐藏文本框", "Tap": "轻触", "Tap (Hold)": "持续按住",
        ": Skip": "：快进", "CG Viewer": "CG 浏览", "Randomise Scenes": "随机选择场景", "Shuffle Scenes": "打乱场景顺序",
        "Randomise Images": "随机选择图片", ": Next CG,": "：下一张 CG，", ": Prev CG": "：上一张 CG",
        ": Prev CG,": "：上一张 CG，", ": Next Scene,": "：下一个场景，", ": Previous Scene,": "：上一个场景，",
        ": Toggle Slideshow Mode,": "：切换幻灯片播放，", ": Exit": "：退出", "Close": "关闭", "General": "常规",
        "Scene": "场景", "Scene Select Columns:": "场景列表列数：", "Scene Select Rows:": "场景列表行数：",
        "BGM": "背景音乐", "SE": "音效", "Voice": "语音", "Spine Animations": "Spine 动画",
        "Touch Controls": "触屏操作", "Pause on Focus Loss": "失去焦点时暂停", "Enable Translations": "启用已有译文",
        "Skip Animations": "跳过动画", "Text Box Under": "文本框放在画面下方", "Cut voice": "推进时停止语音",
        "Copy Text": "复制文本", "Translate Names": "使用已有角色译名", "Play Next Related": "播放下一关联场景",
        "Straight To Action": "跳到重点场景", "Auto Mode": "自动播放", "Wait for Voice": "等待语音结束",
        "Start in Auto": "进入场景时自动播放", "CPS": "每秒字符数", "Slideshow Mode": "幻灯片播放",
        "Fade Effect": "淡入淡出", "Slideshow Time Per CG (ms)": "每张 CG 停留时间（毫秒）",
        "Scale": "缩放比例", "Loading Screen": "显示加载画面", "File Loaders": "并行加载数",
        "Save": "保存", "Exit": "退出", "Skip": "快进", "Select: Scene": "选择：场景", "Select: Story": "选择：剧情",
        "Mode: Scene": "模式：场景", "Mode: CG": "模式：CG", "Mode: Slide": "模式：幻灯片",
        "Random": "随机", "Shuffle": "打乱顺序", "Load File:": "加载文件：", "Backup": "备份",
        "Every:": "间隔：", "seconds": "秒", "TL MODE ACTIVE": "译文编辑模式", "Translator": "译者", "Language": "语言",
        "Save File": "保存文件", "Batch TL Names": "批量转换角色名", "Test Script": "测试脚本", "Stop Test": "停止测试",
        "Add Screen": "添加对白页", "Delete Screen": "删除对白页", "Discard": "放弃修改", "Yes": "是", "No": "否",
        "Unsaved changes in script.": "脚本有尚未保存的修改。", "Continue without saving?": "不保存并继续？",
        "Recovered unsaved script": "已恢复未保存的脚本", "Save script?": "是否保存脚本？",
        "The CG viewer will cycle through all scenes that appear in the scene select menu, so if you've done a search you won't see anything that didn't come up in the search result.":
            "CG 浏览会依次播放当前场景列表中的内容。使用搜索筛选后，只会显示搜索结果中的场景。",
        ": Instead of carrying on to the scene that appears next in scene select a random scene from scene select is chosen instead. This is random so it's possible to come across one scene multiple times before another.":
            "：从当前列表随机选择场景，而不是按列表顺序播放。同一场景可能重复出现。",
        ": The scenes from scene select will be shuffled upon opening the CG viewer, so they'll be in a random order but also fixed in that order until the CG viewer is reopened.":
            "：打开 CG 浏览时打乱场景列表。此次浏览期间顺序固定，重新打开后再次打乱。",
        ": No order at all it just selects a random scene from scene select and a random image from that scene.":
            "：随机选择一个场景，再随机选择其中一张图片。",
        "There is no backlog when one of the random options are selected, going backwards in one of the random modes will give you a random scene/image not the previous CG/Scene.":
            "随机模式不记录播放历史，后退时也会随机选择场景或图片。",
        "The Play Scene button will start the scene that the current CG is from.": "“播放场景”按钮会打开当前 CG 所属的场景。",
        "Press the search button to open the search menu.": "点击搜索按钮打开搜索菜单。",
        "Certain characters can be searched by multiple forms, e.g. Young Asagi, Clone Asagi, Adult Asagi, Characters like this also have a base searchable name which will search for all of the forms, usually the characters full name, e.g. Igawa Asagi.":
            "部分角色可按不同形态搜索，也可用角色的基本名称搜索全部形态。搜索词需使用资料中的原名或别名。",
        "CVs are also searchable by the characters they voice, e.g. searching Yukikaze VA will return results for Himuro Yuri.":
            "配音演员也可通过其配音角色搜索，例如输入角色名并加上 VA。",
        "Seperate values with a comma (,) e.g. Asagi, Sakura, Murasaki.": "多个条件用英文逗号（,）分隔，例如 Asagi, Sakura, Murasaki。",
        "Exclude values from search results by starting them with either an exclamation mark (!) or dash (-).":
            "在条件前加英文感叹号（!）或短横线（-），可排除对应结果。",
        "Inputs with a Match All checkbox next to them make it so that a scene must match ALL of the tags specified to appear as a result, otherwise just a single match is fine.":
            "勾选“全部匹配”后，场景必须包含所有指定标签；未勾选时，匹配任意一个即可。",
        "Match all does not apply to exclusions, searching !Big Breasts, !Dark Skin will exclude all scenes tagged with Big Breasts OR Dark Skin not Big Breasts AND Dark Skin.":
            "排除条件不受“全部匹配”影响。多个排除条件会排除带有任意一个指定标签的场景。",
        "Ticking Exclusive Characters only searches for characters that debuted in RPGX or TABA, this also includes character forms.":
            "“仅独占角色”筛选 RPGX 或 TABA 首次登场的角色，也包括角色的不同形态。",
        "Ticking Main Series Characters only searches for characters that debuted outside of RPGX and TABA.":
            "“仅正篇角色”筛选最初在 RPGX 和 TABA 以外作品中登场的角色。",
        "Ticking Favourites Only searches for the scenes you have favourited.": "“仅收藏”只显示已收藏的场景。",
        "Ticking Translated Only searches for scenes that have an available translation.": "“仅已有译文”只显示已附带译文的场景。",
        "Pressing the Reset button will reset the scene select menu back to its default.": "点击重置按钮可恢复默认场景列表。"
    };
    var excluded = "script,style,title,textarea,select,option,optgroup,#copy,[contenteditable]:not([contenteditable='false'])," +
        ".text-box,.text-box-text,.name-plate,#text-box,#text-box-content,#name-plate,#scene-backlog," +
        "#scene-choice-box,#scene-choice-box-wrapper,.autocomplete-value,.autocomplete-alias,[data-rika-translation]";

    function translated(text) {
        var key = text.replace(/\s+/g, " ").trim(), result = labels[key];
        if (!Object.prototype.hasOwnProperty.call(labels, key)) {
            var section = /^Section (\d+)$/.exec(key);
            if (!section) return null;
            result = "第 " + section[1] + " 节";
        }
        return text.match(/^\s*/)[0] + result + text.match(/\s*$/)[0];
    }

    window.RikaViewerUiFactory = function () {
        var active = false, observer = null, timer = null, applied = 0;
        var textRecords = new Map(), attributeRecords = new Map(), roots = new Set();

        function eligible(element) {
            if (!element || !element.isConnected || element.closest(excluded)) return false;
            if (element.tagName === "INPUT" && /^(hidden|password)$/i.test(element.type)) return false;
            // Hidden source templates must stay in the original language. The
            // Viewer clones them when opening menus, so localize their live copies.
            if (!element.getClientRects().length) return false;
            var visibility = window.getComputedStyle(element).visibility;
            return visibility !== "hidden" && visibility !== "collapse";
        }
        function applyText(node) {
            var current = node.nodeValue, record = textRecords.get(node);
            if (record && current === record.rendered) return;
            textRecords.delete(node);
            var value = translated(current);
            if (value == null || value === current) return;
            textRecords.set(node, { source: current, rendered: value });
            node.nodeValue = value; applied++;
        }
        function applyAttribute(element, key) {
            var property = key === "buttonValue";
            var current = property ? element.value : element.getAttribute(key);
            if (current == null) return;
            var records = attributeRecords.get(element) || new Map(), record = records.get(key);
            if (record && record.rendered === current) return;
            records.delete(key);
            var value = translated(current);
            if (value == null || value === current) return;
            records.set(key, { source: current, rendered: value });
            attributeRecords.set(element, records);
            if (property) element.value = value;
            else element.setAttribute(key, value);
            applied++;
        }
        function scan(root) {
            if (!root || !root.isConnected) return;
            if (root.nodeType === 3) {
                if (eligible(root.parentElement)) applyText(root);
                return;
            }
            if (root.nodeType !== 1 || root.closest(excluded)) return;
            var nodes = [], attributes = [], visibility = new Map();
            function visible(element) {
                if (!visibility.has(element)) visibility.set(element, eligible(element));
                return visibility.get(element);
            }
            var walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
            for (var node = walker.nextNode(); node; node = walker.nextNode()) {
                if (node.nodeValue.trim() && visible(node.parentElement)) nodes.push(node);
            }
            var elements = [root].concat(Array.from(root.querySelectorAll("[title],[placeholder],[aria-label],input[type='button'],input[type='submit'],input[type='reset']")));
            elements.forEach(function (element) { if (visible(element)) attributes.push(element); });
            nodes.forEach(applyText);
            attributes.forEach(function (element) {
                ["title", "placeholder", "aria-label"].forEach(function (key) { applyAttribute(element, key); });
                if (element.tagName === "INPUT" && /^(button|submit|reset)$/i.test(element.type)) applyAttribute(element, "buttonValue");
            });
        }
        function observe() {
            if (active && document.body) observer.observe(document.body, { childList: true, subtree: true, characterData: true,
                attributes: true, attributeFilter: ["title", "placeholder", "aria-label", "value"] });
        }
        function flush() {
            timer = null;
            if (!active || !document.body) return;
            if (!observer) observer = new MutationObserver(function (records) {
                records.forEach(function (record) {
                    if (record.type === "childList") record.addedNodes.forEach(queue);
                    else queue(record.target);
                });
            });
            observer.disconnect();
            try {
                var pending = Array.from(roots); roots.clear(); pending.forEach(scan);
                textRecords.forEach(function (_, node) { if (!node.isConnected) textRecords.delete(node); });
                attributeRecords.forEach(function (_, element) { if (!element.isConnected) attributeRecords.delete(element); });
            } finally { observe(); }
        }
        function queue(root) {
            if (!active || !root || !root.isConnected) return;
            var element = root.nodeType === 3 ? root.parentElement : root;
            if (!element || element.nodeType !== 1 || element.closest(excluded) || element.matches("img,canvas,video,audio,source,link,meta")) return;
            roots.add(root);
            if (roots.size > 64) { roots.clear(); roots.add(document.body); }
            if (timer == null) timer = setTimeout(flush, 40);
        }
        function restore() {
            clearTimeout(timer); timer = null; roots.clear();
            if (observer) observer.disconnect();
            textRecords.forEach(function (record, node) {
                if (node.isConnected && node.nodeValue === record.rendered) node.nodeValue = record.source;
            });
            attributeRecords.forEach(function (records, element) {
                if (!element.isConnected) return;
                records.forEach(function (record, key) {
                    if (key === "buttonValue") {
                        if (element.value === record.rendered) element.value = record.source;
                    } else if (element.getAttribute(key) === record.rendered) element.setAttribute(key, record.source);
                });
            });
            textRecords.clear(); attributeRecords.clear();
        }
        ["click", "keydown", "change"].forEach(function (event) {
            document.addEventListener(event, function () { queue(document.body); }, true);
        });
        document.addEventListener("DOMContentLoaded", function () { queue(document.body); });
        return {
            setEnabled: function (value) {
                value = value === true;
                if (active === value) { if (active) queue(document.body); return; }
                active = value;
                if (active) queue(document.body);
                else restore();
            },
            getState: function () { return { enabled: active, applied: applied, labels: Object.keys(labels).length }; }
        };
    };
}());
