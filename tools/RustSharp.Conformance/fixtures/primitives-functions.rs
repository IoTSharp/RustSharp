mod math {
    pub fn adjust(value: i32, enabled: bool) -> i32 {
        if enabled { value * 2 + 1 } else { value - 1 }
    }
}
use math::adjust as apply;
fn main() {
    let mut value = 20;
    value = apply(value, true);
    let answer: i32 = if value == 41 { value + 1 } else { 0 };
    println!("{}", answer);
    println!("{}", answer == 42 && !(answer < 0));
}
