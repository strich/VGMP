---
layout: default
title: "The Video Game Metadata Project"
---
<div id="total-size" style="font-weight: bold; margin-bottom: 10px;"></div>
<div id="moby-connections" style="font-weight: bold; margin-bottom: 10px;"></div>
<div style="margin-bottom: 10px;">
    <label for="toggle-duplicates" style="margin-right: 10px;">Filter out duplicates:</label>
    <input type="checkbox" id="toggle-duplicates">
</div>
<button id="clear-filters" style="margin-top: 10px;">Clear Filters</button>
<table id="data-table" class="display nowrap" style="width:100%">
    <thead>
        <tr>
            <th>ID</th>
            <th>Game Title</th>
            <th>Alt Game Title</th>
            <th>Disc ID</th>
            <th>Platform</th>
            <th>Region</th>
            <th>Language</th>
            <th>Version</th>
            <th>File CRC</th>
            <th>Moby ID</th>
            <th>Moby ID Confidence</th>
            <th>Moby ID Verified</th>
            <th>Moby Score</th>
            <th>Critic Score</th>
            <th>User Score</th>
            <th>Source Db Last Updated</th>
            <th>Last Updated</th>
            <th>File Name</th>
            <th>Size</th>
            <th>Category</th>
            <th>Platforms</th>
        </tr>
        <tr data-dt-order="disable">
            <th></th>
            <th></th>
            <th></th>
            <th></th>
            <th></th>
            <th><select id="region-filter" multiple="multiple" style="width: 100%;"></select></th>
            <th><select id="language-filter" multiple="multiple" style="width: 100%;"></select></th>
            <th></th>
            <th></th>
            <th></th>
            <th></th>
            <th></th>
            <th></th>
            <th></th>
            <th></th>
            <th></th>
            <th></th>
            <th></th>
            <th></th>
            <th><select id="category-filter" multiple="multiple" style="width: 100%;"></select></th>
            <th><select id="platforms-filter" multiple="multiple" style="width: 100%;"></select></th>
        </tr>
    </thead>
</table>
<script>
    var gamesData = [
        {% for item in site.data.RedumpGamesDb %}
        {
            "ID": "{{ item.ID }}",
            "GameTitle": "{{ item.GameTitle | replace: '"', '\"' }}",
            "AlternativeGameTitle": "{{ item.AlternativeGameTitle | replace: '"', '\"' }}",
            "DiscID": "{{ item.DiscID }}",
            "Platform": "{{ item.Platform }}",
            "Region": "{{ item.Region }}",
            "Language": "{{ item.Language }}",
            "Version": "{{ item.Version }}",
            "FileCRC": "{{ item.FileCRC }}",
            "MobyID": "{{ item.MobyID }}",
            "MobyIDConfidence": "{{ item.MobyIDConfidence }}",
            "MobyIDVerified": "{{ item.MobyIDVerified }}",
            "MobyScore": "{{ site.data.MobyGamesDb | where: "Id", item.MobyID | map: "MobyScore" | first | default: "-" }}",
            "CriticScore": "{{ site.data.MobyGamesDb | where: "Id", item.MobyID | map: "CriticScore" | first | default: "-" }}",
            "UserScore": "{{ site.data.MobyGamesDb | where: "Id", item.MobyID | map: "UserScore" | first | default: "-" }}",
            "SourceDbLastUpdatedUTC": "{{ item.SourceDbLastUpdatedUTC }}",
            "LastUpdatedUTC": "{{ item.LastUpdatedUTC }}",
            "FileName": "{{ item.FileName | replace: '"', '\"' }}",
            "FileSizeBytes": "{{ item.FileSizeBytes }}",
            "Category": "{{ item.Category }}",
            "Platforms": "{{ site.data.MobyGamesDb | where: "Id", item.MobyID | map: "Platforms" | first | default: "" }}"
        }{% unless forloop.last %},{% endunless %}
        {% endfor %}
    ];
</script>
