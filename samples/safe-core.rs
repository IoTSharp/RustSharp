mod math {
    pub fn adjust(value: i32, enabled: bool) -> i32 {
        if enabled { value * 2 + 1 } else { value - 1 }
    }
}

use math::adjust;

fn main() {
    let mut value = 20;
    value = adjust(value, true);
    let answer: i32 = if value == 41 { value + 1 } else { 0 };
    println!("Safe core on .NET");
    println!("{}", answer);
    println!("{}", answer == 42 && !(answer < 0));
}
