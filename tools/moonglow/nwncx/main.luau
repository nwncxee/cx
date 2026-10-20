local mg = require("@moonglow")
local features = require("settings")

-- An area's local variables are its GIT's VarTable (the game reads them from there, not the ARE).
-- Type is the engine's: 1 int, 2 float, 3 string.
local INT, FLOAT, STRING = 1, 2, 3

local function prefix(feature): string
    return `cx.{feature.id}.`
end

local function starts(name: string, p: string): boolean
    return string.lower(string.sub(name, 1, #p)) == p
end

local function area_label(ctx, git: string): string
    local resref = string.sub(git, 1, -5)
    local are = `{resref}.are`
    if ctx.module:has(are) then
        local name = ctx.module:gff(are).Name
        local text = type(name) == "table" and name[0] or nil
        if text and text ~= "" then
            return `{resref} ({text})`
        end
    end
    return resref
end

-- The feature's variables the area has now, by key.
local function current(doc, feature): { [string]: any }
    local found = {}
    local vars = doc.VarTable
    if vars == nil then
        return found
    end
    local p = prefix(feature)
    for _, v in vars.value do
        local name = v.Name and v.Name.value
        if type(name) == "string" and starts(name, p) and v.Value then
            found[string.lower(string.sub(name, #p + 1))] = v.Value.value
        end
    end
    return found
end

local function variable(name: string, setting, value)
    if setting.kind == "check" then
        return { __struct_id = 0, Name = mg.string(name), Type = mg.dword(INT), Value = mg.int(value and 1 or 0) }
    elseif setting.kind == "number" then
        return { __struct_id = 0, Name = mg.string(name), Type = mg.dword(FLOAT), Value = mg.float(value) }
    end
    return { __struct_id = 0, Name = mg.string(name), Type = mg.dword(STRING), Value = mg.string(tostring(value)) }
end

-- Plugins > NWNCX:EE Area Settings...: pick an area and a feature, then set its values or clear them.
mg.command("area-settings", function(ctx)
    local gits = ctx.module:resources("git")
    if #gits == 0 then
        ctx.ui:message("This module has no areas.")
        return
    end
    local labels, git_of = {}, {}
    for _, git in gits do
        local label = area_label(ctx, git)
        table.insert(labels, label)
        git_of[label] = git
    end
    local titles, feature_of = {}, {}
    for _, f in features do
        table.insert(titles, f.title)
        feature_of[f.title] = f
    end
    local pick = ctx.ui:form({
        title = "NWNCX:EE area settings",
        fields = {
            { id = "area", label = "Area", type = "choice", choices = labels, default = labels[1] },
            { id = "feature", label = "Setting", type = "choice", choices = titles, default = titles[1] },
        },
    })
    if not pick then
        return
    end
    local git, feature = git_of[pick.area], feature_of[pick.feature]
    local doc = ctx.module:raw(git)
    local now = current(doc, feature)
    local held = next(now) ~= nil

    local fields = {
        { id = "__hold", label = `This area sets {feature.title} (off: the player's own settings)`, type = "check", default = held },
    }
    for _, s in feature.settings do
        local v = now[s.key]
        if v == nil then
            v = s.default
        elseif s.kind == "check" then
            v = v ~= 0
        end
        table.insert(fields, { id = s.key, label = s.label, type = s.kind, default = v, min = s.min, max = s.max, choices = s.choices })
    end
    local answers = ctx.ui:form({ title = `{feature.title}: {pick.area}`, fields = fields })
    if not answers then
        return
    end

    local p = prefix(feature)
    local vars = doc.VarTable or { type = "list", value = {} }
    local kept = {}
    for _, v in vars.value do
        local name = v.Name and v.Name.value
        if not (type(name) == "string" and starts(name, p)) then
            table.insert(kept, v)
        end
    end
    if answers.__hold then
        for _, s in feature.settings do
            table.insert(kept, variable(p .. s.key, s, answers[s.key]))
        end
    end
    if not held and not answers.__hold then
        return
    end
    vars.value = kept
    doc.VarTable = vars
    ctx.edit:write_gff(git, doc)
    local verb = answers.__hold and "Set" or "Clear"
    ctx.log:info(`{verb} {feature.title} on {pick.area}`)
    return { label = `{verb} NWNCX:EE {feature.title} on {pick.area}` }
end)

-- Build > Verify Module: every cx.* variable of an area names a setting this plugin knows, with a value it takes.
mg.check("cx-variables", function(ctx)
    local findings = {}
    local by_prefix = {}
    for _, f in features do
        by_prefix[prefix(f)] = f
    end
    for _, git in ctx.module:resources("git") do
        local vars = ctx.module:raw(git).VarTable
        for i, v in (vars and vars.value or {}) do
            local name = v.Name and v.Name.value
            if type(name) ~= "string" or not starts(name, "cx.") then
                continue
            end
            local at = `/VarTable[{i - 1}]`
            local lower = string.lower(name)
            local feature, key = nil, nil
            for p, f in by_prefix do
                if starts(lower, p) then
                    feature, key = f, string.sub(lower, #p + 1)
                end
            end
            local setting = nil
            for _, s in (feature and feature.settings or {}) do
                if s.key == key then
                    setting = s
                end
            end
            local value = v.Value and v.Value.value
            local kind = v.Type and v.Type.value
            local message = nil
            if setting == nil then
                message = `{name} names no NWNCX:EE setting`
            elseif value == nil then
                message = `{name} has no value`
            elseif setting.kind == "check" and (kind ~= INT or (value ~= 0 and value ~= 1)) then
                message = `{name} must be an int, 0 or 1`
            elseif setting.kind == "number" and ((kind ~= FLOAT and kind ~= INT) or type(value) ~= "number") then
                message = `{name} must be a float`
            elseif setting.kind == "number" and (value < setting.min or value > setting.max) then
                message = `{name} = {value} is outside {setting.min}..{setting.max}`
            elseif setting.kind == "choice" and table.find(setting.choices, value) == nil then
                message = `{name} = {value} is none of {table.concat(setting.choices, ", ")}`
            end
            if message then
                table.insert(findings, { resource = git, at = at, message = message })
            end
        end
    end
    return findings
end)
