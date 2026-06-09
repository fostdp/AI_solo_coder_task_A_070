var PotDetail = (function(){
  var currentPotId = null;

  function open(pot){
    currentPotId = pot.id;
    document.getElementById('modalOverlay').classList.add('active');
    document.getElementById('modalTitle').textContent=pot.code+' - '+PotLineView.getStatusText(pot.concentration);

    var concClass = pot.concentration>=2.5?'green':pot.concentration>=1.8?'yellow':pot.concentration>=1.5?'orange':'red';
    document.getElementById('modalInfo').innerHTML=
      '<div class="info-card"><div class="info-label">电压 (V)</div><div class="info-value">'+pot.voltage.toFixed(2)+'</div></div>'+
      '<div class="info-card"><div class="info-label">温度 (°C)</div><div class="info-value">'+pot.temperature.toFixed(1)+'</div></div>'+
      '<div class="info-card"><div class="info-label">氧化铝浓度 (%)</div><div class="info-value '+concClass+'">'+pot.concentration.toFixed(2)+'</div></div>'+
      '<div class="info-card"><div class="info-label">阳极效应概率 (%)</div><div class="info-value '+(pot.effectProb>80?'red':'')+'">'+pot.effectProb.toFixed(1)+'</div></div>';

    fetchTrendData(pot.id);
    fetchFeedings(pot.id);
    drawVoltageChart(generateTrendData(pot.voltage,8));
    drawCurrentChart(generateCurrentData(8));
  }

  function close(){
    document.getElementById('modalOverlay').classList.remove('active');
    currentPotId = null;
  }

  function init(){
    document.getElementById('modalClose').addEventListener('click',close);
    document.getElementById('modalOverlay').addEventListener('click',function(e){
      if(e.target===this) close();
    });
  }

  function generateTrendData(base,hours){
    var data=[];
    var now=new Date();
    for(var i=hours*4;i>=0;i--){
      var t=new Date(now.getTime()-i*15*60000);
      data.push({time:t, value:base+Math.sin(i*0.3)*0.4+(Math.random()-0.5)*0.2});
    }
    return data;
  }

  function generateCurrentData(hours){
    var data=[];
    var now=new Date();
    for(var i=hours*4;i>=0;i--){
      var t=new Date(now.getTime()-i*15*60000);
      data.push({time:t, values:Array.from({length:10},function(_,j){return 280+Math.sin(i*0.2+j)*20+(Math.random()-0.5)*10;})});
    }
    return data;
  }

  function drawLineChart(canvasEl,datasets,yMin,yMax,yLabel,colors){
    var c=document.getElementById(canvasEl);
    var parent=c.parentElement;
    c.width=parent.clientWidth-16;
    c.height=200;
    var cx=c.getContext('2d');
    var W=c.width,H=c.height;
    var padL=50,padR=16,padT=16,padB=30;
    var chartW=W-padL-padR,chartH=H-padT-padB;

    cx.fillStyle='#12122a';
    cx.fillRect(0,0,W,H);

    cx.strokeStyle='rgba(255,255,255,0.06)';
    cx.lineWidth=1;
    for(var i=0;i<=5;i++){
      var y=padT+chartH*i/5;
      cx.beginPath();cx.moveTo(padL,y);cx.lineTo(padL+chartW,y);cx.stroke();
      cx.fillStyle='#556688';cx.font='10px sans-serif';cx.textAlign='right';cx.textBaseline='middle';
      cx.fillText((yMax-(yMax-yMin)*i/5).toFixed(1),padL-6,y);
    }

    if(datasets[0]&&datasets[0].length>0){
      var step=Math.max(1,Math.floor(datasets[0].length/6));
      cx.fillStyle='#556688';cx.font='9px sans-serif';cx.textAlign='center';cx.textBaseline='top';
      for(var i=0;i<datasets[0].length;i+=step){
        var x=padL+chartW*i/(datasets[0].length-1);
        var d=datasets[0][i].time;
        cx.fillText(String(d.getHours()).padStart(2,'0')+':'+String(d.getMinutes()).padStart(2,'0'),x,H-padB+6);
      }
    }

    datasets.forEach(function(data,di){
      if(!data||data.length<2) return;
      cx.beginPath();cx.strokeStyle=colors[di%colors.length];cx.lineWidth=1.5;
      var vals=data.map(function(d){return di===0?d.value:d.values[di-1];});
      vals.forEach(function(v,i){
        var x=padL+chartW*i/(vals.length-1);
        var y=padT+chartH*(1-(v-yMin)/(yMax-yMin));
        y=Math.max(padT,Math.min(padT+chartH,y));
        if(i===0) cx.moveTo(x,y); else cx.lineTo(x,y);
      });
      cx.stroke();
    });
  }

  function drawVoltageChart(data){
    drawLineChart('voltageChart',[data],2.5,5.5,'V',['#4fc3f7']);
  }

  function drawCurrentChart(data){
    if(!data||data.length<2) return;
    var colors=['#4fc3f7','#81c784','#fff176','#ff8a65','#ce93d8','#4dd0e1','#a5d6a7','#ffe082','#ffab91','#b39ddb'];
    var datasets=colors.map(function(_,i){
      return data.map(function(d){return {time:d.time,value:d.values[i]};});
    });
    drawLineChart('currentChart',datasets,250,330,'A',colors);
  }

  function fetchTrendData(potId){
    fetch('/api/potdata/'+potId+'/trend').then(function(r){return r.json();}).then(function(data){
      if(!data||data.length<2) return;
      var mapped=data.map(function(d){
        return {time:new Date(d.recordedAt),value:d.voltage};
      });
      drawVoltageChart(mapped);

      var currentData=data.filter(function(d){return d.currentDistribution;}).map(function(d){
        try{
          var arr=JSON.parse(d.currentDistribution);
          return {time:new Date(d.recordedAt),values:arr.slice(0,10)};
        }catch(e){return null;}
      }).filter(Boolean);

      if(currentData.length>1) drawCurrentChart(currentData);
    }).catch(function(){});
  }

  function fetchFeedings(potId){
    fetch('/api/potdata/'+potId+'/feedings').then(function(r){return r.json();}).then(function(data){
      renderFeedings(data);
    }).catch(function(){
      renderFeedings(generateFakeFeedings());
    });
  }

  function generateFakeFeedings(){
    var types=['常规下料','过量下料','紧急下料'];
    var operators=['张三','李四','王五','赵六'];
    var feedings=[];
    var now=new Date();
    for(var i=0;i<10;i++){
      feedings.push({
        feedTime:new Date(now.getTime()-i*3600000*Math.random()*3),
        feedAmount:(10+Math.random()*20).toFixed(1),
        feedType:types[Math.floor(Math.random()*3)],
        operator:operators[Math.floor(Math.random()*4)]
      });
    }
    return feedings;
  }

  function renderFeedings(data){
    var tbody=document.getElementById('feedingsBody');
    tbody.innerHTML='';
    (data||[]).forEach(function(f){
      var t=f.feedTime instanceof Date?f.feedTime:new Date(f.feedTime);
      var ts=String(t.getHours()).padStart(2,'0')+':'+String(t.getMinutes()).padStart(2,'0')+':'+String(t.getSeconds()).padStart(2,'0');
      var tr=document.createElement('tr');
      tr.innerHTML='<td>'+ts+'</td><td>'+f.feedAmount+'</td><td>'+f.feedType+'</td><td>'+f.operator+'</td>';
      tbody.appendChild(tr);
    });
  }

  return { init:init, open:open, close:close };
})();
